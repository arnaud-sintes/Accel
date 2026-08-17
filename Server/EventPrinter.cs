using System.Text.Json;

namespace Accel.Server;

/// <summary>
/// Prints one timestamped, human-readable line per received lifecycle event (session-start/end,
/// subagent-start/stop) to the terminal. Status-line and subagent-status-line events fire on
/// every UI refresh tick and are deliberately never printed here - console output stays minimal
/// during normal execution. Every field access is defensive: malformed/partial/unexpected-shape
/// JSON must never throw.
/// </summary>
public static class EventPrinter
{
    private const int MaxRawBodyChars = 500;

    /// <summary>Prints a lifecycle event (session-start/end, subagent-start/stop, ...).</summary>
    public static void PrintEvent(string eventName, string rawBody)
    {
        Print(eventName, rawBody);
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
