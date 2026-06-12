using System.Globalization;
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

    /// <summary>Path to the skip-unchanged scan cache, or null to disable
    /// caching (the bare-root test ctor sets this to null).</summary>
    private readonly string? _scanCachePath;

    /// <summary>Days to keep events.jsonl.bak-* backups. 0 = keep forever.</summary>
    private readonly int _backupRetentionDays;

    /// <summary>Whether to skip re-scanning sessions whose events.jsonl is
    /// unchanged since the last run.</summary>
    private readonly bool _skipUnchanged;

    public SessionRepairService(ISettingsService settings)
    {
        _sessionRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".copilot", "session-state");
        var repair = settings.Current.Repair;
        _skipUnchanged = repair.SkipUnchangedSessions;
        _scanCachePath = _skipUnchanged
            ? Path.Combine(settings.AppDataDirectory, "state", "repair-scan-cache.json")
            : null;
        _backupRetentionDays = repair.BackupRetentionDays;
    }

    /// <summary>Test-only ctor — bare session root, no scan cache, no pruning.</summary>
    internal SessionRepairService(string sessionRoot)
        : this(sessionRoot, stateDir: null, backupRetentionDays: 0, skipUnchanged: false)
    {
    }

    /// <summary>Test-only ctor with the full set of knobs.</summary>
    internal SessionRepairService(string sessionRoot, string? stateDir, int backupRetentionDays, bool skipUnchanged)
    {
        _sessionRoot = sessionRoot;
        _skipUnchanged = skipUnchanged;
        _scanCachePath = (skipUnchanged && stateDir is not null)
            ? Path.Combine(stateDir, "repair-scan-cache.json")
            : null;
        _backupRetentionDays = backupRetentionDays;
    }

    public IReadOnlyList<RepairResult> RepairAll()
    {
        var results = new List<RepairResult>();
        if (!Directory.Exists(_sessionRoot)) return results;

        // Skip-unchanged cache: events.jsonl fingerprint (mtime+size) from the
        // last scan. A session whose log is byte-for-byte unchanged was already
        // found clean (or already repaired) and need not be re-streamed — this
        // avoids re-reading gigabytes of session logs on every launch.
        var priorCache = _skipUnchanged ? LoadScanCache() : EmptyCache();
        var nextCache = EmptyCache();

        DateTime? backupCutoff = _backupRetentionDays > 0
            ? DateTime.UtcNow.AddDays(-_backupRetentionDays)
            : null;

        foreach (var dir in Directory.EnumerateDirectories(_sessionRoot))
        {
            // Prune stale backups regardless of whether we re-scan the session.
            if (backupCutoff is DateTime cutoff)
                PruneBackupsInDir(dir, cutoff);

            var eventsPath = Path.Combine(dir, "events.jsonl");
            var fingerprint = TryFingerprint(eventsPath);

            if (_skipUnchanged
                && fingerprint is not null
                && priorCache.TryGetValue(eventsPath, out var prior)
                && string.Equals(prior, fingerprint, StringComparison.Ordinal))
            {
                // Unchanged since last scan → carry the fingerprint forward and
                // skip the read entirely.
                nextCache[eventsPath] = fingerprint;
                results.Add(new RepairResult
                {
                    SessionId = new DirectoryInfo(dir).Name,
                    EventsFilePath = eventsPath,
                    DanglingCount = 0,
                    Skipped = true,
                    SkipReason = "unchanged since last scan",
                    Patched = false,
                });
                continue;
            }

            var result = RepairOne(dir);
            results.Add(result);

            // Remember the post-scan fingerprint for sessions we actually
            // processed (clean or patched). Lock / not-found / failure skips are
            // NOT cached, so they're retried on the next launch.
            if (_skipUnchanged && !result.Skipped)
            {
                var after = TryFingerprint(eventsPath);
                if (after is not null) nextCache[eventsPath] = after;
            }
        }

        if (_skipUnchanged) SaveScanCache(nextCache);
        return results;
    }

    private static Dictionary<string, string> EmptyCache() =>
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>events.jsonl identity = "&lt;lastWriteUtcTicks&gt;:&lt;length&gt;".
    /// Null when the file is missing or unreadable.</summary>
    private static string? TryFingerprint(string eventsPath)
    {
        try
        {
            var fi = new FileInfo(eventsPath);
            if (!fi.Exists) return null;
            return fi.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)
                + ":" + fi.Length.ToString(CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }

    private Dictionary<string, string> LoadScanCache()
    {
        if (_scanCachePath is null) return EmptyCache();
        try
        {
            if (!File.Exists(_scanCachePath)) return EmptyCache();
            var json = File.ReadAllText(_scanCachePath);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return parsed is null
                ? EmptyCache()
                : new Dictionary<string, string>(parsed, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            // Corrupt/unreadable cache → treat as empty (everything re-scans).
            return EmptyCache();
        }
    }

    private void SaveScanCache(Dictionary<string, string> cache)
    {
        if (_scanCachePath is null) return;
        try
        {
            var dir = Path.GetDirectoryName(_scanCachePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(cache);
            var tmp = _scanCachePath + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(_scanCachePath))
                File.Replace(tmp, _scanCachePath, destinationBackupFileName: null);
            else
                File.Move(tmp, _scanCachePath);
        }
        catch
        {
            // Best-effort — a missed cache write just means a re-scan next time.
        }
    }

    /// <summary>Delete <c>events.jsonl.bak-*</c> backups older than the cutoff.
    /// Best-effort; backups within the retention window stay as a recovery net.</summary>
    private static void PruneBackupsInDir(string sessionDir, DateTime cutoffUtc)
    {
        IEnumerable<string> backups;
        try { backups = Directory.EnumerateFiles(sessionDir, "events.jsonl.bak-*"); }
        catch { return; }

        foreach (var bak in backups)
        {
            try
            {
                if (File.GetLastWriteTimeUtc(bak) < cutoffUtc)
                    File.Delete(bak);
            }
            catch
            {
                // Best-effort — skip files we can't stat or delete.
            }
        }
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

        // PASS 1 — stream-scan for dangling tool calls WITHOUT materializing
        // the file. events.jsonl can be hundreds of MB (multi-hundred-MB
        // sessions are real). The previous approach parsed every line into a
        // List<JsonObject> and then copied that list, ballooning the working
        // set into the gigabytes at startup (and the OS never reclaimed it).
        // We now retain only small per-tool-call metadata (ids, the start line
        // number, and the start's timestamp/id), so memory stays proportional
        // to the number of tool calls — not the file size.
        DanglingScan scan;
        try
        {
            scan = ScanForDangling(eventsPath);
        }
        catch (IOException)
        {
            return new RepairResult
            {
                SessionId = sessionId,
                EventsFilePath = eventsPath,
                Skipped = true,
                SkipReason = "read failed",
            };
        }

        if (scan.Dangling.Count == 0)
        {
            return new RepairResult
            {
                SessionId = sessionId,
                EventsFilePath = eventsPath,
                DanglingCount = 0,
                Patched = false,
            };
        }

        var model = scan.LastModel ?? DefaultModel;

        // Map raw-line-number -> synthetic completion line to write AFTER it.
        // Keyed by the raw line ordinal (every line counts, including blank/
        // malformed ones) so PASS 2 can stream and insert without re-parsing.
        // Each start has a distinct line number, so there are no collisions.
        var insertAfter = new Dictionary<long, string>();
        foreach (var d in scan.Dangling)
        {
            var synthetic = SynthesizeComplete(d.ToolCallId, d.InteractionId, model, d.StartTimestamp, d.StartId);
            insertAfter[d.StartLineNo] = synthetic.ToJsonString();
        }

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
                DanglingCount = scan.Dangling.Count,
                Skipped = true,
                SkipReason = "backup creation failed",
            };
        }

        // PASS 2 — stream-rewrite to a temp file, inserting each synthetic
        // completion immediately after its tool.execution_start line. Original
        // lines are copied verbatim (lossless — unlike the old path this never
        // drops blank/malformed lines); we only ADD synthetic lines. Memory
        // stays flat (one line at a time). Restore from backup on failure.
        var tmp = eventsPath + ".tmp";
        try
        {
            using (var writer = new StreamWriter(tmp, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.NewLine = "\n";
                long lineNo = 0;
                foreach (var rawLine in File.ReadLines(eventsPath))
                {
                    writer.WriteLine(rawLine);
                    if (insertAfter.TryGetValue(lineNo, out var synthetic))
                        writer.WriteLine(synthetic);
                    lineNo++;
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
                DanglingCount = scan.Dangling.Count,
                Skipped = true,
                SkipReason = "write failed; restored from backup",
            };
        }

        return new RepairResult
        {
            SessionId = sessionId,
            EventsFilePath = eventsPath,
            DanglingCount = scan.Dangling.Count,
            Patched = true,
        };
    }

    /// <summary>
    /// One dangling tool call discovered while streaming: the raw line number
    /// of its <c>tool.execution_start</c> (counting every line so PASS 2 can
    /// insert without re-parsing), the toolCallId, the interactionId from the
    /// originating assistant.message, and the start event's timestamp + id
    /// (echoed into the synthetic completion).
    /// </summary>
    private readonly record struct DanglingInfo(
        long StartLineNo, string ToolCallId, string InteractionId, string StartTimestamp, string? StartId);

    /// <summary>Result of the streaming scan: the dangling calls plus the most
    /// recent model seen (used for the synthetic completion's <c>model</c>).</summary>
    private sealed class DanglingScan
    {
        public List<DanglingInfo> Dangling { get; } = new();
        public string? LastModel { get; set; }
    }

    /// <summary>
    /// Stream <c>events.jsonl</c> exactly once, retaining only small per-tool-
    /// call metadata, to find tool calls that were requested + started but
    /// never completed. Never holds more than one parsed line at a time, so a
    /// multi-hundred-MB session no longer costs gigabytes of RAM. The raw line
    /// numbers recorded here line up with PASS 2's <see cref="File.ReadLines(string)"/>
    /// enumeration (same primitive, same file) so synthetic completions are
    /// inserted in the right place even past blank/malformed lines.
    /// </summary>
    private static DanglingScan ScanForDangling(string eventsPath)
    {
        // toolCallId -> interactionId (from assistant.message.toolRequests[])
        var requested = new Dictionary<string, string>(StringComparer.Ordinal);
        // toolCallId -> (raw line number of its start, start timestamp, start id)
        var starts = new Dictionary<string, (long LineNo, string Ts, string? Id)>(StringComparer.Ordinal);
        var completed = new HashSet<string>(StringComparer.Ordinal);
        string? lastModel = null;

        long lineNo = 0;
        foreach (var rawLine in File.ReadLines(eventsPath))
        {
            var idx = lineNo;
            lineNo++;
            if (string.IsNullOrWhiteSpace(rawLine)) continue;

            JsonObject? ev;
            try { ev = JsonNode.Parse(rawLine) as JsonObject; }
            catch (JsonException) { continue; }
            if (ev is null) continue;

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
                            starts[tid] = (idx, ev["timestamp"]?.GetValue<string>() ?? "", ev["id"]?.GetValue<string>());
                        break;
                    }
                case "tool.execution_complete":
                    {
                        if (data["toolCallId"]?.GetValue<string>() is string tid && !string.IsNullOrEmpty(tid))
                            completed.Add(tid);
                        if (data["model"]?.GetValue<string>() is string m && !string.IsNullOrEmpty(m))
                            lastModel = m;
                        break;
                    }
                case "session.model_change":
                    {
                        if (data["newModel"]?.GetValue<string>() is string m && !string.IsNullOrEmpty(m))
                            lastModel = m;
                        break;
                    }
            }
        }

        var scan = new DanglingScan { LastModel = lastModel };
        foreach (var (tid, iid) in requested)
        {
            if (completed.Contains(tid)) continue;
            if (!starts.TryGetValue(tid, out var s)) continue;
            scan.Dangling.Add(new DanglingInfo(s.LineNo, tid, iid, s.Ts, s.Id));
        }
        return scan;
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
    internal static JsonObject SynthesizeComplete(string toolCallId, string interactionId, string model, string startTimestamp, string? startId)
    {
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
