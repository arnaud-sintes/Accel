using System.Text.Json;

namespace Glaude.Server;

/// <summary>
/// Prints one timestamped, human-readable line per received event to the terminal.
/// Every field access is defensive: malformed/partial/unexpected-shape JSON must never throw.
/// </summary>
public static class EventPrinter
{
    private const int MaxRawBodyChars = 500;

    // Per-session_id status-line throttle. project-plan.md's audit flagged that any
    // de-dup/throttle here must be per-session_id, not global - a global throttle would
    // starve concurrent sessions' status-line output.
    private static readonly TimeSpan StatusLineThrottleWindow = TimeSpan.FromMilliseconds(250);
    private static readonly Dictionary<string, DateTime> LastStatusLinePrintedAt = new();
    private static readonly object ThrottleLock = new();

    /// <summary>Prints a non-statusline event (session-start/end, subagent-start/stop, ...).</summary>
    public static void PrintEvent(string eventName, string rawBody)
    {
        Print(eventName, rawBody);
    }

    /// <summary>
    /// Prints a status-line event, subject to the per-session_id throttle: if the same
    /// session_id's status-line event was printed within the last 250ms, this print is
    /// skipped (the caller still returns 204 regardless). If session_id is absent, never
    /// throttle - print every time rather than guessing at an identity.
    /// </summary>
    public static void PrintStatusLine(string rawBody)
    {
        string? sessionId = TryExtractTopLevelString(rawBody, "session_id");

        if (sessionId is not null)
        {
            lock (ThrottleLock)
            {
                var now = DateTime.UtcNow;
                if (LastStatusLinePrintedAt.TryGetValue(sessionId, out var lastPrinted)
                    && now - lastPrinted < StatusLineThrottleWindow)
                {
                    return;
                }

                LastStatusLinePrintedAt[sessionId] = now;
            }
        }

        Print("StatusLine", rawBody);
    }

    /// <summary>
    /// Prints one summary line per visible task in a `subagentStatusLine` payload's `tasks`
    /// array. Every field on a task entry is optional/nullable per project.md - `id`, `name`,
    /// `type`, `status`, `description`, `label`, `startTime` are always attempted, and
    /// `model`, `effort`, `contextWindowSize`, `tokenCount`, `tokenSamples`, `cwd` are printed
    /// whenever present regardless of version (the payload is the source of truth for what
    /// arrived; version-gating only controls whether the hook is registered at all - see
    /// <c>Glaude.Versioning.VersionGate.ShouldRegisterSubagentStatusLine</c>).
    ///
    /// Prints nothing when `tasks` is missing/absent/empty, so an idle refresh tick (or a
    /// payload from a Claude Code version that sends no tasks) never spams the console. A
    /// single malformed task entry is skipped rather than aborting the whole batch, and a
    /// wholly malformed/non-JSON body is silently ignored - this must never throw.
    /// </summary>
    public static void PrintSubagentStatusLine(string rawBody)
    {
        JsonDocument? doc;
        try
        {
            doc = JsonDocument.Parse(rawBody);
        }
        catch (JsonException)
        {
            return;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (!root.TryGetProperty("tasks", out var tasksProp) || tasksProp.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            if (tasksProp.GetArrayLength() == 0)
            {
                return;
            }

            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");

            foreach (var task in tasksProp.EnumerateArray())
            {
                try
                {
                    string line = FormatTask(task);
                    Console.WriteLine($"[{timestamp}] SubagentStatusLine {line}");
                }
                catch
                {
                    // A single malformed task entry must never abort the rest of the batch.
                }
            }
        }
    }

    private static string FormatTask(JsonElement task)
    {
        if (task.ValueKind != JsonValueKind.Object)
        {
            return "(malformed task entry)";
        }

        var fields = new List<string>();

        AppendStringField(fields, task, "id");
        AppendStringField(fields, task, "name");
        AppendStringField(fields, task, "type");
        AppendStringField(fields, task, "status");
        AppendStringField(fields, task, "description");
        AppendStringField(fields, task, "label");
        AppendStringField(fields, task, "startTime");
        AppendModelField(fields, task);
        // `effort` can be a level string or a numeric token budget - AppendStringField
        // already tolerates both String and Number kinds.
        AppendStringField(fields, task, "effort");
        AppendStringField(fields, task, "contextWindowSize");
        AppendStringField(fields, task, "tokenCount");
        AppendStringField(fields, task, "cwd");
        AppendArrayCountField(fields, task, "tokenSamples");

        return fields.Count > 0 ? string.Join(" ", fields) : "(no fields)";
    }

    // tokenSamples is documented as present but its element shape isn't specified - print a
    // count rather than guessing at a shape, and stay tolerant of it being present but not an
    // array (some other type entirely) by simply not printing anything for it in that case.
    private static void AppendArrayCountField(List<string> fields, JsonElement root, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (!root.TryGetProperty(propertyName, out var prop) || prop.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        fields.Add($"{propertyName}Count={prop.GetArrayLength()}");
    }

    private static string? TryExtractTopLevelString(string rawBody, string propertyName)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(propertyName, out var prop)
                && prop.ValueKind == JsonValueKind.String)
            {
                return prop.GetString();
            }
        }
        catch (JsonException)
        {
            // Malformed JSON - treated as "no session_id available", not thrown.
        }

        return null;
    }

    private static void Print(string eventName, string rawBody)
    {
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");

        JsonDocument? doc;
        try
        {
            doc = JsonDocument.Parse(rawBody);
        }
        catch (JsonException)
        {
            doc = null;
        }

        if (doc is null)
        {
            Console.WriteLine($"[{timestamp}] {eventName} (unparsed body): {Truncate(rawBody)}");
            return;
        }

        using (doc)
        {
            var root = doc.RootElement;
            var fields = new List<string>();

            AppendStringField(fields, root, "session_id");
            AppendStringField(fields, root, "agent_id");
            AppendStringField(fields, root, "agent_type");
            AppendModelField(fields, root);
            AppendStringField(fields, root, "reason");
            AppendStringField(fields, root, "hook_event_name");

            string fieldsText = fields.Count > 0 ? " " + string.Join(" ", fields) : string.Empty;
            Console.WriteLine($"[{timestamp}] {eventName}{fieldsText}");
        }
    }

    private static void AppendStringField(List<string> fields, JsonElement root, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (!root.TryGetProperty(propertyName, out var prop))
        {
            return;
        }

        string? value = prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString(),
            JsonValueKind.Number => prop.GetRawText(),
            JsonValueKind.True or JsonValueKind.False => prop.GetRawText(),
            _ => null,
        };

        if (!string.IsNullOrEmpty(value))
        {
            fields.Add($"{propertyName}={value}");
        }
    }

    // model can show up as either a "model" object with an "id" (statusLine payloads:
    // model.id / model.display_name) or a bare "model" string (some hook payloads) -
    // handle both tolerantly, per project.md's model/effort/context sourcing notes.
    private static void AppendModelField(List<string> fields, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (!root.TryGetProperty("model", out var modelProp))
        {
            return;
        }

        switch (modelProp.ValueKind)
        {
            case JsonValueKind.Object:
                if (modelProp.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
                {
                    fields.Add($"model.id={idProp.GetString()}");
                }
                break;
            case JsonValueKind.String:
                fields.Add($"model={modelProp.GetString()}");
                break;
        }
    }

    private static string Truncate(string body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return "(empty)";
        }

        return body.Length <= MaxRawBodyChars
            ? body
            : body[..MaxRawBodyChars] + "...(truncated)";
    }
}
