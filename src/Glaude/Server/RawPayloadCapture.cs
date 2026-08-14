using System.Threading;

namespace Glaude.Server;

/// <summary>
/// Phase 3b-i: optional "payload-capture mode" (`glaude run --dump-raw &lt;dir&gt;`).
///
/// When enabled, every incoming event route writes the raw request body exactly as
/// received to its own file under the target directory, in addition to (never instead
/// of) the existing Phase 3 printing/handling. This exists purely to unblock Phase 7's
/// manual capture session, which needs real hook payloads on disk to resolve the
/// [EXPERIMENT] items documented in project.md's "Model/Effort/Context metrics sourcing".
///
/// Never lets a write failure (disk full, permissions, invalid path, ...) propagate -
/// callers must still return 204 to the hook caller regardless of capture outcome, per
/// the same tolerant philosophy as the rest of the Phase 3 server.
/// </summary>
public sealed class RawPayloadCapture
{
    private readonly string _dir;
    private long _counter = -1;

    public RawPayloadCapture(string dir)
    {
        _dir = dir;
    }

    /// <summary>
    /// Writes <paramref name="rawBody"/> exactly as received to a new file under the
    /// capture directory. File name embeds a millisecond timestamp, the event name, and
    /// a monotonically increasing counter, so bursts of same-millisecond events never
    /// collide: "&lt;yyyyMMdd-HHmmss-fff&gt;_&lt;event-name&gt;_&lt;counter&gt;.json".
    /// Swallows all exceptions - a capture failure must never crash the server or block
    /// the caller's 204 response.
    /// </summary>
    public void TryWrite(string eventName, string rawBody)
    {
        try
        {
            Directory.CreateDirectory(_dir);

            long counter = Interlocked.Increment(ref _counter);
            string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            string safeEventName = SanitizeForFileName(eventName);
            string fileName = $"{timestamp}_{safeEventName}_{counter}.json";
            string path = Path.Combine(_dir, fileName);

            File.WriteAllText(path, rawBody);
        }
        catch
        {
            // Intentionally swallowed - see class summary. Disk full, permission denial,
            // invalid path, etc. must never crash the server or delay the 204 response.
        }
    }

    private static string SanitizeForFileName(string eventName)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return string.IsNullOrEmpty(eventName)
            ? "unknown-event"
            : new string(eventName.Select(c => invalid.Contains(c) ? '-' : c).ToArray());
    }
}
