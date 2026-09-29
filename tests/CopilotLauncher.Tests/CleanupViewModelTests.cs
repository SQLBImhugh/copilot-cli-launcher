using System.Text;
using CopilotLauncher.Models;
using CopilotLauncher.Services;
using CopilotLauncher.ViewModels;
using Xunit;

namespace CopilotLauncher.Tests;

public sealed class CleanupViewModelTests : IDisposable
{
    private readonly string _tmp;
    private readonly string _root;

    public CleanupViewModelTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "copilot-launcher-tests-" + Guid.NewGuid());
        _root = Path.Combine(_tmp, "session-state");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); }
        catch { /* best effort */ }
    }

    /// <summary>Creates a session folder so the "empty" check has something real to read.</summary>
    private CopilotSession Session(
        string id, string? name = null, string cwd = @"C:\repos\app",
        int eventBytes = 100_000, int summaries = 0, bool locked = false, bool userNamed = false,
        bool hasUserMessage = true)
    {
        var dir = Path.Combine(_root, id);
        Directory.CreateDirectory(dir);
        if (eventBytes > 0)
            File.WriteAllText(Path.Combine(dir, "events.jsonl"), BuildTranscript(eventBytes, hasUserMessage));
        if (locked) File.WriteAllText(Path.Combine(dir, "inuse.1.lock"), "");
        return new CopilotSession
        {
            Id = id, FolderPath = dir, LastModified = DateTime.UtcNow,
            Name = name, Cwd = cwd, SummaryCount = summaries,
            IsLocked = locked, UserNamed = userNamed, SizeBytes = eventBytes,
        };
    }

    /// <summary>
    /// A transcript of roughly <paramref name="approxBytes"/>, padded with the startup
    /// events the CLI writes before any conversation happens.
    /// </summary>
    private static string BuildTranscript(int approxBytes, bool hasUserMessage)
    {
        var sb = new StringBuilder();
        sb.AppendLine("""{"type":"session.start","data":{"sessionId":"x"}}""");
        if (hasUserMessage)
            sb.AppendLine("""{"type":"user.message","data":{"content":"do the thing"}}""");
        while (sb.Length < approxBytes)
            sb.AppendLine("""{"type":"session.warning","data":{"message":"padding"}}""");
        return sb.ToString();
    }

    private static List<CleanupRow> Classify(params CopilotSession[] s) => CleanupViewModel.Classify(s);

    [Fact]
    public void Classify_EmptySessionsByMissingOrTinyTranscript()
    {
        var rows = Classify(
            Session("no-events", name: "something", eventBytes: 0),
            Session("tiny", name: "something", eventBytes: 500),
            Session("real", name: "something", eventBytes: 100_000));

        Assert.Equal(SessionCleanupKind.Empty, rows.Single(r => r.SessionId == "no-events").Kind);
        Assert.Equal(SessionCleanupKind.Empty, rows.Single(r => r.SessionId == "tiny").Kind);
        Assert.NotEqual(SessionCleanupKind.Empty, rows.Single(r => r.SessionId == "real").Kind);
    }

    /// <summary>
    /// `copilot --resume=&lt;id&gt;` mints a placeholder session alongside the one it was
    /// asked to resume. It boots fully — startup, permission, model, warning and hook
    /// events — so it clears the old 2 KB size threshold despite holding no conversation.
    /// </summary>
    [Fact]
    public void Classify_ResumePlaceholderWithoutUserMessageIsEmpty()
    {
        var rows = Classify(
            Session("placeholder", name: "something", eventBytes: 5_226, hasUserMessage: false),
            Session("used", name: "something", eventBytes: 5_226, hasUserMessage: true));

        Assert.Equal(SessionCleanupKind.Empty, rows.Single(r => r.SessionId == "placeholder").Kind);
        Assert.NotEqual(SessionCleanupKind.Empty, rows.Single(r => r.SessionId == "used").Kind);
    }

    /// <summary>Transcripts past the scan cap are assumed real rather than read end to end.</summary>
    [Fact]
    public void Classify_LargeTranscriptIsNeverScannedForUserMessages()
    {
        var rows = Classify(Session("big", name: "something", eventBytes: 400_000, hasUserMessage: false));

        Assert.NotEqual(SessionCleanupKind.Empty, rows[0].Kind);
    }

    [Theory]
    [InlineData("hi")]
    [InlineData("Hello")]
    [InlineData("test")]
    [InlineData("--help")]
    [InlineData("/instructions")]
    [InlineData("Reply with exactly: OK")]
    [InlineData("Answer with exactly: OK")]
    [InlineData("Reply with the single word: PINEAPPLE")]
    [InlineData("What is 2+2? Reply with just the number.")]
    [InlineData("Run your probe.")]
    public void Classify_DetectsProbes(string prompt)
    {
        var rows = Classify(Session("p", name: prompt));
        Assert.Equal(SessionCleanupKind.Probe, rows[0].Kind);
    }

    [Theory]
    [InlineData("Fix the failing build in the CI pipeline")]
    [InlineData("Where is the report-gate agent implemented?")]
    public void Classify_DoesNotFlagRealPromptsAsProbes(string prompt)
    {
        var rows = Classify(Session("r", name: prompt));
        Assert.NotEqual(SessionCleanupKind.Probe, rows[0].Kind);
    }

    [Fact]
    public void Classify_DetectsRepeatedPrompts()
    {
        var rows = Classify(
            Session("a", name: "Run the nightly audit"),
            Session("b", name: "Run the nightly audit"),
            Session("c", name: "A one-off question"));

        Assert.Equal(SessionCleanupKind.Duplicate, rows.Single(r => r.SessionId == "a").Kind);
        Assert.Equal(SessionCleanupKind.Duplicate, rows.Single(r => r.SessionId == "b").Kind);
        Assert.Equal(SessionCleanupKind.Normal, rows.Single(r => r.SessionId == "c").Kind);
    }

    [Fact]
    public void Classify_DetectsScratchDirectories()
    {
        var temp = Path.Combine(Path.GetTempPath(), "agent-workspace");
        var rows = Classify(Session("s", name: "Do a thing", cwd: temp));
        Assert.Equal(SessionCleanupKind.Scratch, rows[0].Kind);
    }

    [Fact]
    public void LockedSessionsCanNeverBeSelectedForDeletion()
    {
        var rows = Classify(Session("locked", name: "hi", locked: true));
        Assert.False(rows[0].CanDelete);
        Assert.Contains("cannot delete", rows[0].Detail);
    }

    [Fact]
    public async Task ProtectMyWork_HidesNamedAndHeavySessions()
    {
        var vm = NewVm(
            Session("named", name: "hi", userNamed: true),
            Session("heavy", name: "hi", summaries: 12),
            Session("junk", name: "hi"));

        await vm.RefreshAsync();

        Assert.True(vm.ProtectMyWork);
        Assert.Single(vm.Visible);
        Assert.Equal("junk", vm.Visible[0].SessionId);

        vm.ProtectMyWork = false;
        Assert.Equal(3, vm.Visible.Count);
    }

    [Fact]
    public async Task SetAllSelections_SkipsLockedRows()
    {
        var vm = NewVm(Session("a", name: "hi"), Session("b", name: "hi", locked: true));
        await vm.RefreshAsync();

        vm.SetAllSelections(true);

        Assert.Equal(1, vm.SelectedCount);
        Assert.True(vm.HasSelection);
    }

    [Fact]
    public async Task DeleteSelected_RemovesRowsAndLeavesLockedOnesAlone()
    {
        var vm = NewVm(
            Session("gone1", name: "hi"),
            Session("gone2", name: "hi"),
            Session("stay", name: "hi", locked: true));
        await vm.RefreshAsync();
        vm.SetAllSelections(true);

        var result = vm.DeleteSelected();

        Assert.Equal(2, result.DeletedCount);
        Assert.False(Directory.Exists(Path.Combine(_root, "gone1")));
        Assert.False(Directory.Exists(Path.Combine(_root, "gone2")));
        Assert.True(Directory.Exists(Path.Combine(_root, "stay")));
        Assert.DoesNotContain(vm.Visible, r => r.SessionId == "gone1");
        Assert.Equal(0, vm.SelectedCount);
    }

    [Fact]
    public async Task DeleteSelected_NothingSelected_IsANoOp()
    {
        var vm = NewVm(Session("a", name: "hi"));
        await vm.RefreshAsync();

        var result = vm.DeleteSelected();

        Assert.Equal(0, result.DeletedCount);
        Assert.True(Directory.Exists(Path.Combine(_root, "a")));
    }

    [Fact]
    public async Task SearchFiltersByPromptPathAndId()
    {
        var vm = NewVm(
            Session("alpha", name: "hi", cwd: @"C:\one"),
            Session("beta", name: "hi", cwd: @"C:\two"));
        await vm.RefreshAsync();

        vm.SearchText = "two";
        Assert.Single(vm.Visible);
        Assert.Equal("beta", vm.Visible[0].SessionId);

        vm.SearchText = "alpha";
        Assert.Single(vm.Visible);

        vm.SearchText = "";
        Assert.Equal(2, vm.Visible.Count);
    }

    private CleanupViewModel NewVm(params CopilotSession[] sessions) =>
        new(new FakeDiscovery(sessions, _root), new SessionDeletionService(_root), a => { a(); return Task.CompletedTask; });

    private sealed class FakeDiscovery : ISessionDiscoveryService
    {
        private readonly CopilotSession[] _sessions;
        public FakeDiscovery(CopilotSession[] s, string root) { _sessions = s; SessionRoot = root; }
        public string SessionRoot { get; }
        // Only surface sessions whose folder still exists, mirroring a real rescan.
        public IEnumerable<CopilotSession> Enumerate() => _sessions.Where(s => Directory.Exists(s.FolderPath));
    }
}
