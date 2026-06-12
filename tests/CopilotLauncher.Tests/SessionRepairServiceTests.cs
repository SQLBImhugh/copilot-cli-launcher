using System.Text.Json.Nodes;
using CopilotLauncher.Services;
using Xunit;

namespace CopilotLauncher.Tests;

public class SessionRepairServiceTests : IDisposable
{
    private readonly string _tmpRoot;
    private readonly string _stateRoot;

    public SessionRepairServiceTests()
    {
        _tmpRoot = Path.Combine(Path.GetTempPath(), "copilot-launcher-repair-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_tmpRoot);
        // State dir lives OUTSIDE the session root so it isn't enumerated as a
        // session by RepairAll.
        _stateRoot = Path.Combine(Path.GetTempPath(), "copilot-launcher-repair-state-" + Guid.NewGuid());
        Directory.CreateDirectory(_stateRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpRoot, recursive: true); }
        catch { /* best effort */ }
        try { Directory.Delete(_stateRoot, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public void RepairAll_ReturnsEmpty_WhenRootMissing()
    {
        var svc = NewSvc(Path.Combine(_tmpRoot, "missing"));
        Assert.Empty(svc.RepairAll());
    }

    [Fact]
    public void RepairOne_SkipsLockedSessions()
    {
        var dir = MakeSession("locked", AssistantWithToolRequest("call-abc", "intx-1") + "\n" + ExecStart("call-abc", "ts1", "evt-start-1"));
        File.WriteAllText(Path.Combine(dir, "inuse.1234.lock"), "");
        var result = SessionRepairService.RepairOne(dir);
        Assert.True(result.Skipped);
        Assert.Contains("in use", result.SkipReason ?? "");
        Assert.False(result.Patched);
    }

    [Fact]
    public void RepairOne_SkipsMissingEventsFile()
    {
        var dir = Path.Combine(_tmpRoot, "no-events");
        Directory.CreateDirectory(dir);
        var result = SessionRepairService.RepairOne(dir);
        Assert.True(result.Skipped);
    }

    [Fact]
    public void RepairOne_NoOps_WhenAllToolUsesAreCompleted()
    {
        var dir = MakeSession("complete",
            AssistantWithToolRequest("call-abc", "intx-1") + "\n" +
            ExecStart("call-abc", "ts1", "evt-start") + "\n" +
            ExecComplete("call-abc"));

        var result = SessionRepairService.RepairOne(dir);
        Assert.False(result.Skipped);
        Assert.Equal(0, result.DanglingCount);
        Assert.False(result.Patched);
        Assert.Empty(Directory.GetFiles(dir, "events.jsonl.bak-*"));
    }

    [Fact]
    public void RepairOne_PatchesDanglingToolCall_AndBacksUp()
    {
        var dir = MakeSession("dangling",
            AssistantWithToolRequest("call-abc", "intx-1") + "\n" +
            ExecStart("call-abc", "2026-05-15T10:00:00.000Z", "evt-start-1"));

        var result = SessionRepairService.RepairOne(dir);
        Assert.False(result.Skipped);
        Assert.Equal(1, result.DanglingCount);
        Assert.True(result.Patched);
        Assert.Single(Directory.GetFiles(dir, "events.jsonl.bak-*"));

        var lines = File.ReadAllLines(Path.Combine(dir, "events.jsonl"));
        Assert.Equal(3, lines.Length);  // assistant, start, synthetic-complete
        var synthetic = JsonNode.Parse(lines[2])!.AsObject();
        Assert.Equal("tool.execution_complete", synthetic["type"]!.GetValue<string>());
        Assert.Equal("call-abc", synthetic["data"]!["toolCallId"]!.GetValue<string>());
        Assert.False(synthetic["data"]!["success"]!.GetValue<bool>());
        Assert.Equal("aborted", synthetic["data"]!["error"]!["code"]!.GetValue<string>());
        Assert.Equal("intx-1", synthetic["data"]!["interactionId"]!.GetValue<string>());
        // Synthetic timestamp + parentId echo the start event
        Assert.Equal("2026-05-15T10:00:00.000Z", synthetic["timestamp"]!.GetValue<string>());
        Assert.Equal("evt-start-1", synthetic["parentId"]!.GetValue<string>());
    }

    [Fact]
    public void RepairOne_InsertsAfterStart_NotAtEof()
    {
        // assistant -> start(call-abc) -> [later event] should become
        // assistant -> start(call-abc) -> SYNTHETIC -> [later event]
        var dir = MakeSession("ordering",
            AssistantWithToolRequest("call-abc", "intx-1") + "\n" +
            ExecStart("call-abc", "ts1", "evt-start") + "\n" +
            BareEvent("session.heartbeat"));

        var result = SessionRepairService.RepairOne(dir);
        Assert.True(result.Patched);

        var lines = File.ReadAllLines(Path.Combine(dir, "events.jsonl"));
        Assert.Equal(4, lines.Length);
        Assert.Equal("assistant.message", JsonNode.Parse(lines[0])!["type"]!.GetValue<string>());
        Assert.Equal("tool.execution_start", JsonNode.Parse(lines[1])!["type"]!.GetValue<string>());
        Assert.Equal("tool.execution_complete", JsonNode.Parse(lines[2])!["type"]!.GetValue<string>());
        Assert.Equal("session.heartbeat", JsonNode.Parse(lines[3])!["type"]!.GetValue<string>());
    }

    [Fact]
    public void RepairOne_HandlesMultipleDanglingCalls_WithCorrectOrdering()
    {
        // Two dangling tool calls in different positions; both should get
        // synthetic completions inserted right after their respective starts.
        var dir = MakeSession("multi",
            AssistantWithToolRequest("call-1", "intx-1") + "\n" +
            ExecStart("call-1", "ts1", "evt-1") + "\n" +
            BareEvent("middle.event") + "\n" +
            AssistantWithToolRequest("call-2", "intx-2") + "\n" +
            ExecStart("call-2", "ts2", "evt-2"));

        var result = SessionRepairService.RepairOne(dir);
        Assert.Equal(2, result.DanglingCount);
        Assert.True(result.Patched);

        var lines = File.ReadAllLines(Path.Combine(dir, "events.jsonl"));
        // Original 5 + 2 synthetics = 7
        Assert.Equal(7, lines.Length);
        // After call-1 start (idx 1), synthetic for call-1 at idx 2.
        var s1 = JsonNode.Parse(lines[2])!;
        Assert.Equal("tool.execution_complete", s1["type"]!.GetValue<string>());
        Assert.Equal("call-1", s1["data"]!["toolCallId"]!.GetValue<string>());
        // After call-2 start (originally idx 4, now shifted to idx 5), synthetic at idx 6.
        var s2 = JsonNode.Parse(lines[6])!;
        Assert.Equal("call-2", s2["data"]!["toolCallId"]!.GetValue<string>());
    }

    [Fact]
    public void RepairOne_IsIdempotent()
    {
        var dir = MakeSession("idempotent",
            AssistantWithToolRequest("call-abc", "intx-1") + "\n" +
            ExecStart("call-abc", "ts1", "evt-start"));

        var first = SessionRepairService.RepairOne(dir);
        Assert.Equal(1, first.DanglingCount);
        Assert.True(first.Patched);

        var second = SessionRepairService.RepairOne(dir);
        Assert.Equal(0, second.DanglingCount);
        Assert.False(second.Patched);
    }

    [Fact]
    public void RepairOne_SkipsToolUseWithoutMatchingStart()
    {
        // A tool was requested but never started (e.g., assistant message
        // arrived but the CLI crashed before running). The legacy script also
        // skips these because there's no start event to insert after.
        var dir = MakeSession("no-start",
            AssistantWithToolRequest("call-abc", "intx-1"));
        var result = SessionRepairService.RepairOne(dir);
        Assert.Equal(0, result.DanglingCount);
        Assert.False(result.Patched);
    }

    [Fact]
    public void RepairOne_TolaratesMalformedJsonLines()
    {
        var dir = MakeSession("malformed",
            "not json at all\n" +
            AssistantWithToolRequest("call-abc", "intx-1") + "\n" +
            "{broken\n" +
            ExecStart("call-abc", "ts", "evt") + "\n" +
            ExecComplete("call-abc"));

        var result = SessionRepairService.RepairOne(dir);
        Assert.False(result.Skipped);
        Assert.Equal(0, result.DanglingCount);
    }

    [Fact]
    public void RepairOne_PreservesMalformedAndBlankLines_WhenPatching()
    {
        // A malformed line and a blank line sit between the real events. The
        // streaming repair copies every original line verbatim (it only ADDS
        // the synthetic completion) and still inserts at the correct position
        // despite the unparseable/blank lines shifting raw line numbers — this
        // guards the PASS-1/PASS-2 raw-line-number parity.
        var dir = MakeSession("preserve",
            AssistantWithToolRequest("call-abc", "intx-1") + "\n" +
            "this is not json\n" +
            "\n" +
            ExecStart("call-abc", "2026-05-15T10:00:00.000Z", "evt-start-1"));

        var result = SessionRepairService.RepairOne(dir);
        Assert.True(result.Patched);
        Assert.Equal(1, result.DanglingCount);

        var lines = File.ReadAllLines(Path.Combine(dir, "events.jsonl"));
        // assistant, garbage, blank, start, synthetic = 5
        Assert.Equal(5, lines.Length);
        Assert.Equal("assistant.message", JsonNode.Parse(lines[0])!["type"]!.GetValue<string>());
        Assert.Equal("this is not json", lines[1]);   // malformed line preserved verbatim
        Assert.Equal("", lines[2]);                    // blank line preserved
        Assert.Equal("tool.execution_start", JsonNode.Parse(lines[3])!["type"]!.GetValue<string>());
        var synthetic = JsonNode.Parse(lines[4])!.AsObject();  // inserted AFTER the start
        Assert.Equal("tool.execution_complete", synthetic["type"]!.GetValue<string>());
        Assert.Equal("call-abc", synthetic["data"]!["toolCallId"]!.GetValue<string>());
        Assert.Equal("2026-05-15T10:00:00.000Z", synthetic["timestamp"]!.GetValue<string>());
        Assert.Equal("evt-start-1", synthetic["parentId"]!.GetValue<string>());
    }

    [Fact]
    public void DetectModel_PrefersMostRecentToolCompletion()
    {
        var events = new List<JsonObject>
        {
            JsonNode.Parse(ExecComplete("call-1", model: "claude-old"))!.AsObject(),
            JsonNode.Parse(BareEvent("foo"))!.AsObject(),
            JsonNode.Parse(ExecComplete("call-2", model: "claude-newer"))!.AsObject(),
        };
        Assert.Equal("claude-newer", SessionRepairService.DetectModel(events));
    }

    [Fact]
    public void DetectModel_FallsBack_WhenNothingPresent()
    {
        var events = new List<JsonObject>
        {
            JsonNode.Parse(BareEvent("foo"))!.AsObject(),
        };
        // Default is the const inside the service; we don't expose it, so just
        // assert it's a non-empty plausible model name.
        var m = SessionRepairService.DetectModel(events);
        Assert.False(string.IsNullOrEmpty(m));
    }

    // ─── Skip-unchanged cache ───────────────────────────────────────────────

    [Fact]
    public void RepairAll_SkipsUnchangedSessions_OnSecondRun()
    {
        // A clean session (tool completed) — nothing to patch.
        MakeSession("clean",
            AssistantWithToolRequest("call-abc", "intx-1") + "\n" +
            ExecStart("call-abc", "ts", "evt") + "\n" +
            ExecComplete("call-abc"));

        var svc = new SessionRepairService(_tmpRoot, _stateRoot, backupRetentionDays: 0, skipUnchanged: true);

        var first = Assert.Single(svc.RepairAll());
        Assert.False(first.Skipped);                 // scanned this run
        Assert.Equal(0, first.DanglingCount);

        var second = Assert.Single(svc.RepairAll());
        Assert.True(second.Skipped);                 // unchanged → not re-read
        Assert.Equal("unchanged since last scan", second.SkipReason);
    }

    [Fact]
    public void RepairAll_ReScansSession_AfterEventsFileChanges()
    {
        var dir = MakeSession("changed",
            AssistantWithToolRequest("call-abc", "intx-1") + "\n" +
            ExecStart("call-abc", "ts", "evt") + "\n" +
            ExecComplete("call-abc"));

        var svc = new SessionRepairService(_tmpRoot, _stateRoot, backupRetentionDays: 0, skipUnchanged: true);
        svc.RepairAll();   // populate the cache

        // Mutate the log so its size (and mtime) change.
        File.AppendAllText(Path.Combine(dir, "events.jsonl"), "\n" + BareEvent("session.heartbeat"));

        var rescan = Assert.Single(svc.RepairAll());
        Assert.False(rescan.Skipped);                            // re-scanned because it changed
        Assert.NotEqual("unchanged since last scan", rescan.SkipReason);
    }

    [Fact]
    public void RepairAll_DoesNotSkip_WhenSkipUnchangedDisabled()
    {
        MakeSession("nocache",
            AssistantWithToolRequest("call-abc", "intx-1") + "\n" +
            ExecStart("call-abc", "ts", "evt") + "\n" +
            ExecComplete("call-abc"));

        var svc = new SessionRepairService(_tmpRoot, _stateRoot, backupRetentionDays: 0, skipUnchanged: false);
        svc.RepairAll();
        var second = Assert.Single(svc.RepairAll());
        Assert.False(second.Skipped);                // no caching → always scans
    }

    // ─── Backup pruning ─────────────────────────────────────────────────────

    [Fact]
    public void RepairAll_PrunesBackupsOlderThanRetention()
    {
        // Clean session (requested + completed, no start) so RepairAll itself
        // creates no new backup — we control the backups under test.
        var dir = MakeSession("prune",
            AssistantWithToolRequest("call-abc", "intx-1") + "\n" +
            ExecComplete("call-abc"));

        var oldBak = Path.Combine(dir, "events.jsonl.bak-20200101-000000");
        var newBak = Path.Combine(dir, "events.jsonl.bak-20990101-000000");
        File.WriteAllText(oldBak, "old");
        File.WriteAllText(newBak, "new");
        File.SetLastWriteTimeUtc(oldBak, DateTime.UtcNow.AddDays(-30));
        File.SetLastWriteTimeUtc(newBak, DateTime.UtcNow.AddDays(-1));

        var svc = new SessionRepairService(_tmpRoot, stateDir: null, backupRetentionDays: 7, skipUnchanged: false);
        svc.RepairAll();

        Assert.False(File.Exists(oldBak));   // older than 7 days → pruned
        Assert.True(File.Exists(newBak));    // within the window → kept
    }

    [Fact]
    public void RepairAll_KeepsAllBackups_WhenRetentionZero()
    {
        var dir = MakeSession("keep",
            AssistantWithToolRequest("call-abc", "intx-1") + "\n" +
            ExecComplete("call-abc"));

        var oldBak = Path.Combine(dir, "events.jsonl.bak-20200101-000000");
        File.WriteAllText(oldBak, "old");
        File.SetLastWriteTimeUtc(oldBak, DateTime.UtcNow.AddDays(-365));

        var svc = new SessionRepairService(_tmpRoot, stateDir: null, backupRetentionDays: 0, skipUnchanged: false);
        svc.RepairAll();

        Assert.True(File.Exists(oldBak));    // retention 0 = keep forever
    }

    // ─── Fixture helpers ────────────────────────────────────────────────────

    private string MakeSession(string name, string events)
    {
        var dir = Path.Combine(_tmpRoot, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "events.jsonl"), events);
        return dir;
    }

    private static string AssistantWithToolRequest(string toolCallId, string interactionId) =>
        new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString(),
            ["type"] = "assistant.message",
            ["data"] = new JsonObject
            {
                ["interactionId"] = interactionId,
                ["toolRequests"] = new JsonArray
                {
                    new JsonObject { ["toolCallId"] = toolCallId, ["name"] = "some-tool" },
                },
            },
        }.ToJsonString();

    private static string ExecStart(string toolCallId, string timestamp, string evtId) =>
        new JsonObject
        {
            ["id"] = evtId,
            ["timestamp"] = timestamp,
            ["type"] = "tool.execution_start",
            ["data"] = new JsonObject { ["toolCallId"] = toolCallId },
        }.ToJsonString();

    private static string ExecComplete(string toolCallId, string? model = null)
    {
        var data = new JsonObject { ["toolCallId"] = toolCallId, ["success"] = true };
        if (model is not null) data["model"] = model;
        return new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString(),
            ["type"] = "tool.execution_complete",
            ["data"] = data,
        }.ToJsonString();
    }

    private static string BareEvent(string type) =>
        new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString(),
            ["type"] = type,
            ["data"] = new JsonObject(),
        }.ToJsonString();

    private static SessionRepairService NewSvc(string root)
    {
        var ctor = typeof(SessionRepairService)
            .GetConstructor(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                            null, new[] { typeof(string) }, null);
        return (SessionRepairService)ctor!.Invoke(new object[] { root });
    }
}
