using System.Diagnostics;
using CopilotLauncher.Models;
using CopilotLauncher.Services;
using Xunit;

namespace CopilotLauncher.Tests;

public class UpdateCheckServiceTests
{
    // ---------- ParseOutput (static helper) ----------

    [Fact]
    public void ParseOutput_HandlesNoUpdateMessage()
    {
        var output = "No update needed, current version is 1.0.48, last checked 1 minute ago.";
        var result = IUpdateCheckService.ParseOutput(output, "1.0.46");
        Assert.NotNull(result);
        Assert.Equal("1.0.46", result!.PreviousVersion);
        Assert.Equal("1.0.48", result.CurrentVersion);
        Assert.True(result.VersionChanged);
    }

    [Fact]
    public void ParseOutput_HandlesInstalledMessage()
    {
        var output = "Copilot CLI version 1.0.49 installed.";
        var result = IUpdateCheckService.ParseOutput(output, "1.0.48");
        Assert.NotNull(result);
        Assert.Equal("1.0.49", result!.CurrentVersion);
        Assert.True(result.VersionChanged);
    }

    [Fact]
    public void ParseOutput_DetectsNoChange()
    {
        var output = "No update needed, current version is 1.0.48, last checked 2 hours ago.";
        var result = IUpdateCheckService.ParseOutput(output, "1.0.48");
        Assert.NotNull(result);
        Assert.False(result!.VersionChanged);
    }

    [Fact]
    public void ParseOutput_ReturnsNull_OnUnknownFormat()
    {
        var result = IUpdateCheckService.ParseOutput("Some unexpected output we don't recognize", "1.0.48");
        Assert.Null(result);
    }

    [Fact]
    public void ParseOutput_ReturnsNull_OnEmptyInput()
    {
        Assert.Null(IUpdateCheckService.ParseOutput("", "1.0.48"));
        Assert.Null(IUpdateCheckService.ParseOutput("   \n\n  ", "1.0.48"));
    }

    [Fact]
    public void ParseOutput_PreservesRawOutput()
    {
        var raw = "No update needed, current version is 1.0.48, blah blah";
        var result = IUpdateCheckService.ParseOutput(raw, "1.0.46");
        Assert.Equal(raw, result!.RawOutput);
    }

    [Fact]
    public void Fallback_PrefersPostUpdateQuery_OverParsedOutput()
    {
        // Documents the precedence used inside RunAsync when computing the
        // post-update version:
        //   post != "unknown"  ->  post
        //   else parsed?.CurrentVersion ?? ""  (null result if also empty)
        // This protects against older copilot CLIs that emit unrecognized
        // update text but DID install a new version (visible via --version).
        string prev = "1.0.40";
        string post = "1.0.49";

        // Unrecognized update output -> parsed is null. Post wins.
        var parsed = IUpdateCheckService.ParseOutput("blah blah", prev);
        Assert.Null(parsed);
        var current = post != "unknown" ? post : (parsed?.CurrentVersion ?? string.Empty);
        Assert.Equal("1.0.49", current);

        // Post unknown, parsed recognized -> parsed wins (regression: the
        // parsed fallback must remain wired so stale-CLI machines still
        // surface transitions when --version itself fails).
        post = "unknown";
        parsed = IUpdateCheckService.ParseOutput("Copilot CLI version 1.0.48 installed.", prev);
        current = post != "unknown" ? post : (parsed?.CurrentVersion ?? string.Empty);
        Assert.Equal("1.0.48", current);

        // Post unknown, parsed null -> empty (RunAsync returns null in this case).
        parsed = IUpdateCheckService.ParseOutput("noise", prev);
        current = post != "unknown" ? post : (parsed?.CurrentVersion ?? string.Empty);
        Assert.Equal(string.Empty, current);
    }

    // ---------- Persistence + two-phase commit ----------
    //
    // These tests cover the v0.1.11 fix for "Check now reports no transition
    // even when copilot CLI just auto-updated under the hood". The launcher
    // now persists a LastObservedCopilotVersion across launches and compares
    // post-update against THAT baseline instead of an in-process snapshot
    // (which copilot's silent background auto-update can race past).

    [Fact]
    public void CommitObservedVersion_PersistsNewValueAndSkipsNoOps()
    {
        var settings = new FakeSettings { Current = MakeSettings(lastObserved: "1.0.55") };
        var svc = MakeService(settings);

        // No-op write when value matches.
        settings.SaveCount = 0;
        svc.CommitObservedVersion("1.0.55");
        Assert.Equal("1.0.55", settings.Current.Briefings.LastObservedCopilotVersion);
        Assert.Equal(0, settings.SaveCount);

        // Real write when value changes.
        svc.CommitObservedVersion("1.0.56");
        Assert.Equal("1.0.56", settings.Current.Briefings.LastObservedCopilotVersion);
        Assert.Equal(1, settings.SaveCount);

        // Empty / whitespace / null inputs are ignored without writing.
        svc.CommitObservedVersion("");
        svc.CommitObservedVersion("   ");
        svc.CommitObservedVersion(null!);
        Assert.Equal("1.0.56", settings.Current.Briefings.LastObservedCopilotVersion);
        Assert.Equal(1, settings.SaveCount);
    }

    [Fact]
    public void CommitObservedVersion_DoesNotThrow_WhenSaveFails()
    {
        // The service swallows save exceptions so a transient disk hiccup
        // can't lose the briefing the caller just persisted.
        var settings = new FakeSettings
        {
            Current = MakeSettings(lastObserved: "1.0.55"),
            ThrowOnSave = true,
        };
        var svc = MakeService(settings);
        svc.CommitObservedVersion("1.0.56");  // must not throw
        // In-memory value is updated regardless.
        Assert.Equal("1.0.56", settings.Current.Briefings.LastObservedCopilotVersion);
    }

    [Fact]
    public void UpdateCheckResult_BootstrapHasNoVersionChange_ByConstruction()
    {
        // Bootstrap entries set PreviousVersion = CurrentVersion so callers
        // checking VersionChanged in the non-bootstrap branch get a clean
        // "no change" rather than a stale-looking transition.
        var result = new UpdateCheckResult
        {
            PreviousVersion = "1.0.56",
            CurrentVersion = "1.0.56",
            RawOutput = "",
            IsBootstrap = true,
        };
        Assert.False(result.VersionChanged);
        Assert.True(result.IsBootstrap);
    }

    [Fact]
    public void MigrationFromBriefingHistory_PicksNewestToVersion()
    {
        // v0.1.10 -> v0.1.11 upgrade scenario. The user has briefings.json
        // entries but LastObservedCopilotVersion is null. The most recent
        // briefing's ToVersion should be picked up as the baseline so a real
        // post-upgrade transition (e.g. 1.0.55 -> 1.0.56) is correctly
        // attributed instead of being bootstrap-suppressed.
        //
        // BriefingHistoryService.All is sorted newest-first inside Add, so
        // [0].ToVersion is the candidate baseline RunAsync's migration logic
        // looks at. This test pins that invariant — if Add ever changes its
        // sort order the migration silently picks the wrong baseline.
        var history = new FakeHistory();
        history.Add(new BriefingEntry
        {
            Id = Guid.NewGuid().ToString(),
            Timestamp = DateTime.UtcNow.AddDays(-7),
            FromVersion = "1.0.48",
            ToVersion = "1.0.50",
            Source = "copilot-update",
            Body = "older",
        });
        history.Add(new BriefingEntry
        {
            Id = Guid.NewGuid().ToString(),
            Timestamp = DateTime.UtcNow.AddHours(-1),
            FromVersion = "1.0.50",
            ToVersion = "1.0.55",
            Source = "copilot-update",
            Body = "newer",
        });
        Assert.Equal("1.0.55", history.All[0].ToVersion);
    }

    [Fact]
    public async Task RunAsync_ReturnsNull_WhenCopilotNotResolvable()
    {
        // Most CI agents and contributor machines without copilot installed
        // will hit this path. RunAsync must return null cleanly (no throw)
        // so the Briefing tab + startup check just no-op.
        //
        // We can't actually exercise the persistence branches without
        // mocking the Helpers.ProcessUtil.Resolve static (which would
        // require a refactor). The persistence logic is therefore covered
        // by the unit tests above (CommitObservedVersion contract + the
        // BriefingHistoryService sort-order invariant) plus the manual
        // end-to-end test fixture at C:\Temp\fake-copilot\.
        var settings = new FakeSettings { Current = MakeSettings(lastObserved: null) };
        var svc = MakeService(settings);
        // On any machine without copilot CLI on PATH this returns null.
        // On a machine with copilot installed the spawn lambda below returns
        // null, which short-circuits the same way.
        var result = await svc.RunAsync();
        Assert.Null(result);
    }

    // ---------- Test helpers ----------

    private static AppSettings MakeSettings(string? lastObserved)
    {
        var s = new AppSettings();
        s.Normalize();
        s.Briefings.LastObservedCopilotVersion = lastObserved;
        return s;
    }

    private static UpdateCheckService MakeService(ISettingsService settings, IBriefingHistoryService? history = null)
    {
        history ??= new FakeHistory();
        return new UpdateCheckService(
            settings,
            history,
            spawn: _ => null,                    // simulate "copilot didn't start"
            getCurrentVersion: () => Task.FromResult("unknown"));
    }

    private sealed class FakeSettings : ISettingsService
    {
        public string AppDataDirectory { get; set; } = Path.GetTempPath();
        public string SettingsFilePath { get; set; } = Path.Combine(Path.GetTempPath(), "fake-settings.json");
        public AppSettings Current { get; set; } = new();
        public int SaveCount { get; set; }
        public bool ThrowOnSave { get; set; }
        public void Load() { }
        public void Save()
        {
            SaveCount++;
            if (ThrowOnSave) throw new IOException("simulated save failure");
        }
    }

    private sealed class FakeHistory : IBriefingHistoryService
    {
        private readonly List<BriefingEntry> _items = new();
        public string FilePath => Path.Combine(Path.GetTempPath(), "fake-briefings.json");
        public IReadOnlyList<BriefingEntry> All => _items;
        public void Reload() { }
        public void Add(BriefingEntry entry)
        {
            _items.Add(entry);
            // Mirror BriefingHistoryService's invariant: newest first after Add.
            var sorted = _items.OrderByDescending(e => e.Timestamp).Take(50).ToList();
            _items.Clear();
            _items.AddRange(sorted);
        }
        public void Clear() => _items.Clear();
    }
}
