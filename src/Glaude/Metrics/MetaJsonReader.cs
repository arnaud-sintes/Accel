using System.Text.Json;

namespace Glaude.Metrics;

/// <summary>
/// The optional fields Glaude extracts from a subagent's sibling ".meta.json" file. Per
/// project.md's live-disk tally over 43 real subagent file pairs, every field here is
/// optional (model was present in only 29/43, parentAgentId in only 1/43) - callers must
/// treat every property as possibly absent.
/// </summary>
public sealed record SubagentMetaInfo(
    string? AgentType,
    int? SpawnDepth,
    string? ToolUseId,
    string? Description,
    string? Model,
    string? ParentAgentId);

/// <summary>
/// Reads the ".meta.json" file that sits alongside a subagent transcript
/// ("agent_transcript_path" from a SubagentStop payload) - same directory, same basename,
/// ".meta.json" extension instead of ".jsonl". Never throws: missing file, malformed JSON,
/// or a file that isn't a JSON object all yield null rather than an exception.
/// </summary>
public static class MetaJsonReader
{
    /// <summary>
    /// Given a subagent transcript path (e.g. ".../subagents/agent-&lt;id&gt;.jsonl"),
    /// reads the sibling ".../subagents/agent-&lt;id&gt;.meta.json" file, if present.
    /// </summary>
    public static SubagentMetaInfo? TryRead(string? agentTranscriptPath)
    {
        if (string.IsNullOrEmpty(agentTranscriptPath))
        {
            return null;
        }

        try
        {
            string metaPath = SiblingMetaPath(agentTranscriptPath);

            if (!File.Exists(metaPath))
            {
                return null;
            }

            string text = File.ReadAllText(metaPath);

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string? agentType = GetStringOrNull(root, "agentType");
            int? spawnDepth = GetIntOrNull(root, "spawnDepth");
            string? toolUseId = GetStringOrNull(root, "toolUseId");
            string? description = GetStringOrNull(root, "description");
            string? model = GetStringOrNull(root, "model");
            string? parentAgentId = GetStringOrNull(root, "parentAgentId");

            return new SubagentMetaInfo(agentType, spawnDepth, toolUseId, description, model, parentAgentId);
        }
        catch
        {
            // Missing/inaccessible/malformed file - never throw, just report "no data".
            return null;
        }
    }

    /// <summary>
    /// Computes the sibling ".meta.json" path for a given transcript path, without
    /// relying on <see cref="Path.ChangeExtension(string, string)"/> (whose "replace after
    /// last dot" semantics would mishandle a basename that itself contains a dot).
    /// </summary>
    private static string SiblingMetaPath(string transcriptPath)
    {
        string? directory = Path.GetDirectoryName(transcriptPath);
        string baseName = Path.GetFileNameWithoutExtension(transcriptPath);
        string metaFileName = baseName + ".meta.json";

        return string.IsNullOrEmpty(directory)
            ? metaFileName
            : Path.Combine(directory, metaFileName);
    }

    private static string? GetStringOrNull(JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString();
        }

        return null;
    }

    private static int? GetIntOrNull(JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out var prop)
            && prop.ValueKind == JsonValueKind.Number
            && prop.TryGetInt32(out int value))
        {
            return value;
        }

        return null;
    }
}
