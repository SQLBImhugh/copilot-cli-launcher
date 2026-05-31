using System.Diagnostics;
using System.Text.RegularExpressions;

namespace CopilotLauncher.Services;

public sealed class UpdateCheckResult
{
    public required string PreviousVersion { get; init; }
    public required string CurrentVersion { get; init; }
    public bool VersionChanged => !string.Equals(PreviousVersion, CurrentVersion, StringComparison.Ordinal);

    /// <summary>Raw stdout/stderr from `copilot update`. Useful for diagnostics if parsing fails.</summary>
    public required string RawOutput { get; init; }

    /// <summary>
    /// True when this is the first observation the launcher has ever made
    /// (no <c>LastObservedCopilotVersion</c> in settings AND no prior briefing
    /// to migrate from). Callers should record a baseline without creating a
    /// briefing in this case — the very first check shouldn't fabricate a
    /// "1.0.X → 1.0.X" no-op briefing or, worse, a wrong "→ from unknown"
    /// transition. Subsequent checks will catch real updates against this
    /// baseline. <see cref="VersionChanged"/> is false when bootstrapping
    /// (PreviousVersion == CurrentVersion by construction).
    /// </summary>
    public bool IsBootstrap { get; init; }
}

public interface IUpdateCheckService
{
    /// <summary>
    /// Runs <c>copilot update</c>, parses the output, and reports the version
    /// before/after using the launcher's persisted "last observed" baseline
    /// (NOT a fresh in-process query — copilot's silent background auto-update
    /// can already have advanced the cached version before the user clicks
    /// Check now, making any synchronous prev-query meaningless).
    /// Returns null if the copilot CLI is not on PATH or the post-update
    /// version cannot be determined at all.
    /// <para>
    /// IMPORTANT: this method does NOT persist the new baseline. Callers MUST
    /// invoke <see cref="CommitObservedVersion"/> AFTER successfully recording
    /// a briefing (or after presenting bootstrap UX). This two-phase commit
    /// prevents transitions from being silently lost when briefing rendering
    /// or AI summary generation fails partway through.
    /// </para>
    /// <para>
    /// Bootstrap exception: when the result has <see cref="UpdateCheckResult.IsBootstrap"/>
    /// set, this method ALREADY committed the baseline (there's no briefing to
    /// wait for). Callers should not call <see cref="CommitObservedVersion"/>
    /// in that case, but doing so is harmless (idempotent).
    /// </para>
    /// </summary>
    Task<UpdateCheckResult?> RunAsync(CancellationToken ct = default);

    /// <summary>
    /// Persist the observed version as the new baseline. Idempotent — writes
    /// only when the value actually changes. Call this AFTER the briefing
    /// entry has been successfully persisted so a mid-flight failure doesn't
    /// strand the transition undetectable.
    /// </summary>
    void CommitObservedVersion(string version);

    /// <summary>
    /// Parse copilot's update-output text. Public + static so tests can hit it
    /// without spinning up a process.
    /// </summary>
    static UpdateCheckResult? ParseOutput(string output, string previousVersion) =>
        ParseImpl(output, previousVersion);

    private static UpdateCheckResult? ParseImpl(string output, string previousVersion)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;

        // The CLI emits one of two shapes (see legacy/Launch-Copilot.ps1::Invoke-CopilotUpdate):
        //   "No update needed, current version is X.Y.Z, ..."
        //   "Copilot CLI version X.Y.Z installed."
        // We tolerate both and surface unknown formats with the raw text intact.
        string? newVersion = null;

        var noUpdate = Regex.Match(output, @"No update needed,\s*current version is\s+(?<v>[\d\.]+)", RegexOptions.IgnoreCase);
        if (noUpdate.Success) newVersion = noUpdate.Groups["v"].Value;

        if (newVersion is null)
        {
            var installed = Regex.Match(output, @"Copilot CLI version\s+(?<v>[\d\.]+)\s+installed", RegexOptions.IgnoreCase);
            if (installed.Success) newVersion = installed.Groups["v"].Value;
        }

        if (newVersion is null) return null;

        return new UpdateCheckResult
        {
            PreviousVersion = previousVersion,
            CurrentVersion = newVersion,
            RawOutput = output,
        };
    }
}

public sealed class UpdateCheckService : IUpdateCheckService
{
    private readonly ISettingsService _settings;
    private readonly IBriefingHistoryService _history;
    private readonly Func<ProcessStartInfo, Process?> _spawn;
    private readonly Func<Task<string>> _getCurrentVersion;

    public UpdateCheckService(ISettingsService settings, IBriefingHistoryService history)
        : this(settings, history, Process.Start, GetVersionFromCli) { }

    /// <summary>Test-only ctor.</summary>
    internal UpdateCheckService(
        ISettingsService settings,
        IBriefingHistoryService history,
        Func<ProcessStartInfo, Process?> spawn,
        Func<Task<string>> getCurrentVersion)
    {
        _settings = settings;
        _history = history;
        _spawn = spawn;
        _getCurrentVersion = getCurrentVersion;
    }

    public async Task<UpdateCheckResult?> RunAsync(CancellationToken ct = default)
    {
        // Persisted baseline. Empty when the launcher has never observed a
        // version (fresh install) OR when v0.1.10 -> v0.1.11 upgrade left
        // the field unpopulated. For the upgrade case we migrate from the
        // most recent briefing's ToVersion so returning users keep their
        // continuity instead of getting a fake "first run" bootstrap.
        var prev = _settings.Current.Briefings.LastObservedCopilotVersion ?? string.Empty;
        if (string.IsNullOrWhiteSpace(prev))
        {
            try
            {
                if (_history.All.Count > 0)
                {
                    // BriefingHistoryService.All is sorted newest-first inside Add,
                    // so [0].ToVersion is the most recent transition we recorded
                    // (under the v0.1.10 in-process-prev model).
                    var migrated = _history.All[0].ToVersion;
                    if (!string.IsNullOrWhiteSpace(migrated)) prev = migrated;
                }
            }
            catch
            {
                // Migration is best-effort. If briefings.json is unreadable we'll
                // just treat this as a genuine bootstrap.
            }
        }

        var copilot = Helpers.ProcessUtil.Resolve(TerminalDiscoveryService.ResolveOnPath);
        if (copilot is null) return null;

        var psi = new ProcessStartInfo
        {
            FileName = copilot.FileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in copilot.PrefixArgs) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("update");

        var proc = _spawn(psi);
        if (proc is null) return null;

        // Read stdout and stderr concurrently to avoid pipe-buffer deadlock
        // (one stream blocking on a full buffer while we wait on the other).
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        var combined = (stdout + Environment.NewLine + stderr).Trim();

        // Re-query `copilot --version` after the update exits. This is the
        // source of truth — older copilot builds emit update-completion text
        // we don't recognize (no "Copilot CLI version X.Y.Z installed."
        // line), and a parse-only path would then report stale.
        //
        // NOTE: we deliberately do NOT pass `--no-auto-update`. copilot CLI's
        // own auto-update has already run (either via this `copilot update`
        // call or earlier in the background), so `--version` returns the
        // current cached version, which is what we want. v0.1.10 added the
        // flag thinking it would let us snapshot "prev" before update; but
        // the flag also disables the cache-walk in copilot's gate function,
        // so it returns the immutable npm-bundled version instead. With the
        // persisted-baseline model the prev-snapshot isn't needed anyway.
        var post = await _getCurrentVersion().ConfigureAwait(false);
        var parsed = IUpdateCheckService.ParseOutput(combined, prev);
        var current = post != "unknown"
            ? post
            : (parsed?.CurrentVersion ?? string.Empty);

        if (string.IsNullOrEmpty(current))
        {
            // Couldn't determine the post-update version at all (CLI broken
            // or both --version and update output unparseable). Better to
            // return null than fabricate a transition.
            return null;
        }

        var isBootstrap = string.IsNullOrEmpty(prev);
        if (isBootstrap)
        {
            // First-ever observation. Commit the baseline immediately —
            // there's no briefing to wait for, so the two-phase-commit
            // concern doesn't apply. Future checks will detect transitions
            // against this baseline.
            CommitObservedVersion(current);
            return new UpdateCheckResult
            {
                PreviousVersion = current,
                CurrentVersion = current,
                RawOutput = combined,
                IsBootstrap = true,
            };
        }

        return new UpdateCheckResult
        {
            PreviousVersion = prev,
            CurrentVersion = current,
            RawOutput = combined,
            IsBootstrap = false,
        };
    }

    public void CommitObservedVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version)) return;
        var current = _settings.Current.Briefings.LastObservedCopilotVersion;
        if (string.Equals(current, version, StringComparison.Ordinal)) return;
        _settings.Current.Briefings.LastObservedCopilotVersion = version;
        try { _settings.Save(); }
        catch
        {
            // Best-effort persistence. A failed save means the next check
            // will re-detect the same transition; the user can dismiss the
            // duplicate. Better than throwing here and losing the briefing.
        }
    }

    private static async Task<string> GetVersionFromCli()
    {
        var copilot = Helpers.ProcessUtil.Resolve(TerminalDiscoveryService.ResolveOnPath);
        if (copilot is null) return "unknown";

        var psi = new ProcessStartInfo
        {
            FileName = copilot.FileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in copilot.PrefixArgs) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("--version");

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return "unknown";
            // Drain both streams concurrently to avoid pipe-buffer deadlock.
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync().ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            _ = await stderrTask.ConfigureAwait(false);  // discard but drain
            // Output looks like: "GitHub Copilot CLI 1.0.48."
            var m = Regex.Match(stdout, @"(?<v>\d+\.\d+\.\d+)");
            return m.Success ? m.Groups["v"].Value : "unknown";
        }
        catch
        {
            return "unknown";
        }
    }
}
