using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CopilotLauncher.Services;

/// <summary>
/// One repaired session.
/// </summary>
public sealed class RepairResult
{
    public required string SessionId { get; init; }
    public required string EventsFilePath { get; init; }
    public int DanglingCount { get; init; }

    /// <summary>True if the session was skipped (active lock present, missing file, etc.).</summary>
    public bool Skipped { get; init; }
    public string? SkipReason { get; init; }

    /// <summary>True if changes were written to disk.</summary>
    public bool Patched { get; init; }
}

public interface ISessionRepairService
{
    /// <summary>
    /// Walks every session under the configured root, finds tool calls that
    /// were started but never completed (typical cause: Ctrl+C during a tool
    /// execution), and inserts a synthetic <c>tool.execution_complete</c>
    /// event with <c>success=false</c> + <c>error.code="aborted"</c>
    /// immediately after the matching <c>tool.execution_start</c>. This
    /// satisfies the Anthropic API's tool_use/tool_result pairing invariant
    /// so that <c>copilot --resume</c> doesn't 400.
    ///
    /// Idempotent — a fully-repaired session reports zero dangles on the next
    /// run. Backs up <c>events.jsonl</c> as <c>events.jsonl.bak-YYYYMMDD-HHmmss</c>
    /// before mutating, and restores on write failure. Skips locked sessions
    /// (<c>inuse.*.lock</c> file present).
    ///
    /// Direct port of <c>legacy/repair-copilot-sessions.py</c>; tracks the
    /// real event shape used by the Copilot CLI:
    /// <list type="bullet">
    ///   <item><c>assistant.message.data.toolRequests[].toolCallId</c></item>
    ///   <item><c>tool.execution_start.data.toolCallId</c></item>
    ///   <item><c>tool.execution_complete.data.toolCallId</c></item>
    /// </list>
    /// </summary>
    IReadOnlyList<RepairResult> RepairAll();
}

public sealed class SessionRepairService : ISessionRepairService
{
    /// <summary>Default model used in the synthetic completion when none can be detected.</summary>
    private const string DefaultModel = "claude-opus-4.7";

    /// <summary>Aborted-tool error message embedded in synthetic completions.</summary>
    private const string AbortedMessage =
        "Tool execution was aborted (Ctrl+C, network drop, or hung MCP call). " +
        "Synthesized by CopilotLauncher.SessionRepairService to satisfy the " +
        "Anthropic API tool_use/tool_result pairing invariant.";

    private readonly string _sessionRoot;

    public SessionRepairService()
    {
        _sessionRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".copilot", "session-state");
    }

    /// <summary>Test-only ctor.</summary>
    internal SessionRepairService(string sessionRoot)
    {
        _sessionRoot = sessionRoot;
    }

    public IReadOnlyList<RepairResult> RepairAll()
    {
        var results = new List<RepairResult>();
        if (!Directory.Exists(_sessionRoot)) return results;

        foreach (var dir in Directory.EnumerateDirectories(_sessionRoot))
        {
            results.Add(RepairOne(dir));
        }
        return results;
    }

    /// <summary>
    /// Repair one session folder. Internal so tests can hit individual cases
    /// without standing up a full directory tree under the configured root.
    /// </summary>
    internal static RepairResult RepairOne(string sessionDir)
    {
        var sessionId = new DirectoryInfo(sessionDir).Name;
        var eventsPath = Path.Combine(sessionDir, "events.jsonl");

        if (!File.Exists(eventsPath))
        {
            return new RepairResult
            {
                SessionId = sessionId,
                EventsFilePath = eventsPath,
                Skipped = true,
                SkipReason = "events.jsonl not found",
            };
        }

        // Skip active sessions — never touch a file the live writer has open.
        var hasLock = Directory.EnumerateFiles(sessionDir, "inuse.*.lock").Any();
        if (hasLock)
        {
            return new RepairResult
            {
                SessionId = sessionId,
                EventsFilePath = eventsPath,
                Skipped = true,
                SkipReason = "session is in use (lock file present)",
            };
        }

        // Load + parse all events into memory. Tolerate bad lines (skip them
        // but keep going) so a partial corruption doesn't take down the whole
        // file. The legacy .py does the same.
        var events = new List<JsonObject>();
        foreach (var rawLine in File.ReadLines(eventsPath))
        {
            if (string.IsNullOrWhiteSpace(rawLine)) continue;
            try
            {
                if (JsonNode.Parse(rawLine) is JsonObject obj)
                    events.Add(obj);
            }
            catch (JsonException)
            {
                // Tolerate malformed lines.
            }
        }
        if (events.Count == 0)
        {
            return new RepairResult
            {
                SessionId = sessionId,
                EventsFilePath = eventsPath,
                DanglingCount = 0,
                Patched = false,
            };
        }

        var dangling = FindDangling(events);
        if (dangling.Count == 0)
        {
            return new RepairResult
            {
                SessionId = sessionId,
                EventsFilePath = eventsPath,
                DanglingCount = 0,
                Patched = false,
            };
        }

        var model = DetectModel(events);

        // Build the synthetic events keyed by the start_idx where they should
        // be inserted. We walk inserts in reverse-index order so each insert
        // doesn't shift the indices of subsequent ones.
        var inserts = dangling
            .Select(d => (StartIdx: d.StartIdx, Synthetic: SynthesizeComplete(d.ToolCallId, d.InteractionId, model, events[d.StartIdx])))
            .OrderByDescending(t => t.StartIdx)
            .ToList();

        // Backup before mutating.
        var backup = eventsPath + ".bak-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        try
        {
            File.Copy(eventsPath, backup, overwrite: false);
        }
        catch (IOException)
        {
            // Backup failed — abort to preserve the original file.
            return new RepairResult
            {
                SessionId = sessionId,
                EventsFilePath = eventsPath,
                DanglingCount = dangling.Count,
                Skipped = true,
                SkipReason = "backup creation failed",
            };
        }

        var newEvents = new List<JsonObject>(events);
        foreach (var ins in inserts)
        {
            newEvents.Insert(ins.StartIdx + 1, ins.Synthetic);
        }

        // Atomic write: temp + replace. Restore from backup on write failure.
        var tmp = eventsPath + ".tmp";
        try
        {
            using (var writer = new StreamWriter(tmp, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.NewLine = "\n";
                foreach (var e in newEvents)
                {
                    // Compact JSON to match the legacy script's output.
                    writer.WriteLine(e.ToJsonString());
                }
            }

            if (File.Exists(eventsPath))
                File.Replace(tmp, eventsPath, destinationBackupFileName: null);
            else
                File.Move(tmp, eventsPath);
        }
        catch (IOException)
        {
            // Restore from backup on failure.
            try { File.Copy(backup, eventsPath, overwrite: true); }
            catch { /* best effort */ }
            try { if (File.Exists(tmp)) File.Delete(tmp); }
            catch { /* best effort */ }
            return new RepairResult
            {
                SessionId = sessionId,
                EventsFilePath = eventsPath,
                DanglingCount = dangling.Count,
                Skipped = true,
                SkipReason = "write failed; restored from backup",
            };
        }

        return new RepairResult
        {
            SessionId = sessionId,
            EventsFilePath = eventsPath,
            DanglingCount = dangling.Count,
            Patched = true,
        };
    }

    /// <summary>
    /// One dangling tool call: the index of its start event, the toolCallId,
    /// and the interactionId from the originating assistant.message.
    /// </summary>
    internal readonly record struct DanglingToolCall(int StartIdx, string ToolCallId, string InteractionId);

    internal static List<DanglingToolCall> FindDangling(IReadOnlyList<JsonObject> events)
    {
        // toolCallId -> interactionId (from assistant.message.toolRequests[])
        var requested = new Dictionary<string, string>(StringComparer.Ordinal);
        // toolCallId -> index in events where its tool.execution_start lives
        var starts = new Dictionary<string, int>(StringComparer.Ordinal);
        var completed = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < events.Count; i++)
        {
            var ev = events[i];
            var type = ev["type"]?.GetValue<string>();
            if (string.IsNullOrEmpty(type)) continue;
            if (ev["data"] is not JsonObject data) continue;

            switch (type)
            {
                case "assistant.message":
                    {
                        var iid = data["interactionId"]?.GetValue<string>() ?? "";
                        if (data["toolRequests"] is JsonArray reqs)
                        {
                            foreach (var r in reqs)
                            {
                                if (r is JsonObject ro &&
                                    ro["toolCallId"]?.GetValue<string>() is string tid &&
                                    !string.IsNullOrEmpty(tid))
                                {
                                    requested[tid] = iid;
                                }
                            }
                        }
                        break;
                    }
                case "tool.execution_start":
                    {
                        if (data["toolCallId"]?.GetValue<string>() is string tid && !string.IsNullOrEmpty(tid))
                            starts[tid] = i;
                        break;
                    }
                case "tool.execution_complete":
                    {
                        if (data["toolCallId"]?.GetValue<string>() is string tid && !string.IsNullOrEmpty(tid))
                            completed.Add(tid);
                        break;
                    }
            }
        }

        var result = new List<DanglingToolCall>();
        foreach (var (tid, iid) in requested)
        {
            if (completed.Contains(tid)) continue;
            if (!starts.TryGetValue(tid, out var startIdx)) continue;
            result.Add(new DanglingToolCall(startIdx, tid, iid));
        }
        return result;
    }

    /// <summary>Find the most recent model used in this session, falling back to a default.</summary>
    internal static string DetectModel(IReadOnlyList<JsonObject> events)
    {
        for (int i = events.Count - 1; i >= 0; i--)
        {
            var ev = events[i];
            var type = ev["type"]?.GetValue<string>();
            if (ev["data"] is not JsonObject data) continue;

            if (type == "tool.execution_complete")
            {
                if (data["model"]?.GetValue<string>() is string m && !string.IsNullOrEmpty(m))
                    return m;
            }
            else if (type == "session.model_change")
            {
                if (data["newModel"]?.GetValue<string>() is string m && !string.IsNullOrEmpty(m))
                    return m;
            }
        }
        return DefaultModel;
    }

    /// <summary>Build the synthetic completion event matching the legacy .py output.</summary>
    internal static JsonObject SynthesizeComplete(string toolCallId, string interactionId, string model, JsonObject startEvent)
    {
        var startTimestamp = startEvent["timestamp"]?.GetValue<string>() ?? "";
        var startId = startEvent["id"]?.GetValue<string>();

        return new JsonObject
        {
            ["type"] = "tool.execution_complete",
            ["data"] = new JsonObject
            {
                ["toolCallId"] = toolCallId,
                ["model"] = model,
                ["interactionId"] = interactionId,
                ["success"] = false,
                ["error"] = new JsonObject
                {
                    ["message"] = AbortedMessage,
                    ["code"] = "aborted",
                },
            },
            ["id"] = Guid.NewGuid().ToString(),
            ["timestamp"] = startTimestamp,
            ["parentId"] = startId is null ? null : JsonValue.Create(startId),
        };
    }
}
