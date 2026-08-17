namespace Accel.Settings;

using System.Collections.Generic;
using System.Text.Json.Nodes;

/// <summary>
/// The two independent top-level status-line fields Accel may take over.
/// They follow identical capture/restore rules but are stored separately.
/// </summary>
public enum StatusLineField
{
    StatusLine,
    SubagentStatusLine,
}

/// <summary>
/// What was in a status-line field <b>before</b> Accel took it over.
///
/// <see cref="HadOriginal"/> false means "the field did not exist" — which is a materially
/// different fact from "not captured yet", because uninstall must then <i>remove</i> the field
/// rather than restore anything.
/// </summary>
public sealed class StatusLineCapture
{
    private readonly JsonNode? original;

    private StatusLineCapture(bool hadOriginal, JsonNode? original)
    {
        HadOriginal = hadOriginal;
        this.original = original;
    }

    /// <summary>No pre-existing field was present at capture time.</summary>
    public static StatusLineCapture None { get; } = new(false, null);

    public bool HadOriginal { get; }

    /// <summary>The captured original node (detached clone), or null when there was none.</summary>
    public JsonNode? Original => original;

    /// <summary>Captures the full original node, deep-cloned so it is independent of the DOM.</summary>
    public static StatusLineCapture Capture(JsonNode? node) =>
        node is null ? None : new StatusLineCapture(true, node.DeepClone());

    /// <summary>A fresh detached clone suitable for re-insertion into a DOM.</summary>
    public JsonNode? CloneOriginal() => original?.DeepClone();

    /// <summary>Serialisable shape (a later phase decides where this is persisted).</summary>
    public JsonObject ToJson() => new()
    {
        ["hadOriginal"] = HadOriginal,
        ["original"] = CloneOriginal(),
    };

    public static StatusLineCapture FromJson(JsonObject obj)
    {
        var had = obj["hadOriginal"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        return had ? Capture(obj["original"]) : None;
    }
}

/// <summary>
/// Store for the captured originals. Phase 2 only fixes the data shape and the
/// capture/restore logic; where this lives on disk is a later phase's decision.
/// </summary>
public interface IStatusLineChainStore
{
    bool TryGet(StatusLineField field, out StatusLineCapture capture);

    void Save(StatusLineField field, StatusLineCapture capture);

    void Remove(StatusLineField field);
}

/// <summary>In-memory implementation — sufficient for Phase 2 and for tests.</summary>
public sealed class InMemoryStatusLineChainStore : IStatusLineChainStore
{
    private readonly Dictionary<StatusLineField, StatusLineCapture> captures = new();

    public bool TryGet(StatusLineField field, out StatusLineCapture capture) =>
        captures.TryGetValue(field, out capture!);

    public void Save(StatusLineField field, StatusLineCapture capture) => captures[field] = capture;

    public void Remove(StatusLineField field) => captures.Remove(field);

    public int Count => captures.Count;
}

/// <summary>
/// Capture/restore helpers for a single top-level status-line field. Kept separate from
/// <see cref="SettingsMerger"/> so the chaining rules can be unit-tested on their own.
/// </summary>
public static class StatusLineChain
{
    public static string FieldName(StatusLineField field) => field switch
    {
        StatusLineField.SubagentStatusLine => AccelHookSpec.SubagentStatusLineField,
        _ => AccelHookSpec.StatusLineField,
    };

    public static bool IsAccelOwned(StatusLineField field, JsonNode? node)
    {
        var command = AccelHookSpec.GetCommand(node);
        return field == StatusLineField.SubagentStatusLine
            ? AccelHookSpec.IsAccelSubagentStatusLineCommand(command)
            : AccelHookSpec.IsAccelStatusLineCommand(command);
    }

    /// <summary>
    /// Installs <paramref name="desired"/> into <paramref name="root"/>, capturing whatever was
    /// there first (including "nothing was there") unless a capture already exists or the
    /// current value is already Accel's own — never overwrite a real capture with our own value.
    /// </summary>
    public static void Install(JsonObject root, StatusLineField field, JsonObject desired, IStatusLineChainStore store)
    {
        var name = FieldName(field);
        var current = root[name];
        var currentIsOurs = IsAccelOwned(field, current);

        if (!currentIsOurs && !store.TryGet(field, out _))
        {
            store.Save(field, StatusLineCapture.Capture(current));
        }

        root[name] = desired;
    }

    /// <summary>
    /// Restores the captured original, or removes the field if there was none. A field that is
    /// no longer Accel-owned (someone else took it over after us) is left untouched.
    /// </summary>
    public static void Uninstall(JsonObject root, StatusLineField field, IStatusLineChainStore store)
    {
        var name = FieldName(field);
        var current = root[name];
        var currentIsOurs = IsAccelOwned(field, current);

        if (store.TryGet(field, out var capture))
        {
            if (currentIsOurs)
            {
                if (capture.HadOriginal)
                {
                    root[name] = capture.CloneOriginal();
                }
                else
                {
                    root.Remove(name);
                }
            }

            store.Remove(field);
            return;
        }

        // No capture recorded: if the field is ours, it can only have been added by us.
        if (currentIsOurs)
        {
            root.Remove(name);
        }
    }
}
