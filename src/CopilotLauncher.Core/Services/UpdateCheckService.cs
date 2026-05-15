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
}

public interface IUpdateCheckService
{
    /// <summary>
    /// Runs <c>copilot update</c>, parses the output, and reports the version
    /// before/after. Returns null if the copilot CLI is not on PATH.
    /// </summary>
    Task<UpdateCheckResult?> RunAsync(CancellationToken ct = default);

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
    private readonly Func<ProcessStartInfo, Process?> _spawn;
    private readonly Func<Task<string>> _getCurrentVersion;

    public UpdateCheckService()
        : this(Process.Start, GetVersionFromCli) { }

    /// <summary>Test-only ctor.</summary>
    internal UpdateCheckService(
        Func<ProcessStartInfo, Process?> spawn,
        Func<Task<string>> getCurrentVersion)
    {
        _spawn = spawn;
        _getCurrentVersion = getCurrentVersion;
    }

    public async Task<UpdateCheckResult?> RunAsync(CancellationToken ct = default)
    {
        var prev = await _getCurrentVersion().ConfigureAwait(false);
        if (prev == "unknown") return null;

        var copilot = Helpers.ProcessUtil.Resolve(TerminalDiscoveryService.ResolveOnPath);
        if (copilot is null) return null;

        var psi = new ProcessStartInfo
        {
            FileName = copilot.FileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
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
        return IUpdateCheckService.ParseOutput(combined, prev)
            ?? new UpdateCheckResult { PreviousVersion = prev, CurrentVersion = prev, RawOutput = combined };
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
