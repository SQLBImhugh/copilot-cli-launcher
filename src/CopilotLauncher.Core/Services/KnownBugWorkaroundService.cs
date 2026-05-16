using System.Diagnostics;
using System.Text.RegularExpressions;

namespace CopilotLauncher.Services;

public sealed class WorkaroundResult
{
    public required string Name { get; init; }
    public required bool Applied { get; init; }
    public string? Detail { get; init; }
}

public interface IKnownBugWorkaroundService
{
    /// <summary>
    /// Apply all currently-active known-bug workarounds. Each workaround is
    /// idempotent (safe to re-run) and self-checks: if not needed, returns
    /// without mutating anything. Best-effort: failures are logged but
    /// don't block app startup.
    /// </summary>
    [Obsolete("Use ApplyAllAsync instead.")]
    IReadOnlyList<WorkaroundResult> ApplyAll();

    /// <summary>
    /// Async version of <see cref="ApplyAll"/>. Prefer this so startup work
    /// doesn't block while shelling out to the Copilot CLI.
    /// </summary>
    Task<IReadOnlyList<WorkaroundResult>> ApplyAllAsync(CancellationToken ct = default);
}

public sealed class KnownBugWorkaroundService : IKnownBugWorkaroundService
{
    /// <summary>
    /// Hand-written stub for the missing win32-native loader, taken verbatim
    /// from legacy/Launch-Copilot.ps1. Re-applied after every CLI update
    /// since each update overwrites the version directory.
    /// </summary>
    private const string Win32LoaderStub =
@"// Workaround for https://github.com/github/copilot-cli/issues/3298
// The auto-generated NAPI-RS loader for win32-native is missing from the
// prebuild bundle. This hand-written shim loads the right .node binary so
// /keep-alive (and getErrorMode, enableCrashReporting, installExceptionFilter)
// work on Windows. Re-applied on app start by CopilotLauncher.
const path = require('node:path')
let nativeBinding = null
const loadErrors = []
if (process.platform === 'win32') {
  if (process.arch === 'x64') {
    try { nativeBinding = require('./win32-native.win32-x64-msvc.node') }
    catch (e) { loadErrors.push(e) }
  } else if (process.arch === 'arm64') {
    try { nativeBinding = require('./win32-native.win32-arm64-msvc.node') }
    catch (e) { loadErrors.push(e) }
  } else {
    loadErrors.push(new Error(`Unsupported Windows architecture: ${process.arch}`))
  }
} else {
  module.exports = {}
  return
}
if (!nativeBinding) {
  throw new Error(`Failed to load win32-native: ${loadErrors.map(e => e.message).join('; ')}`)
}
module.exports = nativeBinding
";

    private readonly ISettingsService _settings;

    public KnownBugWorkaroundService(ISettingsService settings)
    {
        _settings = settings;
    }

    [Obsolete("Use ApplyAllAsync instead.")]
    public IReadOnlyList<WorkaroundResult> ApplyAll() => ApplyAllAsync().GetAwaiter().GetResult();

    public async Task<IReadOnlyList<WorkaroundResult>> ApplyAllAsync(CancellationToken ct = default)
    {
        var results = new List<WorkaroundResult>();
        if (!_settings.Current.Repair.ApplyWin32KeepAliveWorkaround)
        {
            results.Add(new WorkaroundResult { Name = "win32-keep-alive", Applied = false, Detail = "disabled in settings" });
            return results;
        }

        try
        {
            results.Add(await ApplyWin32KeepAliveLoaderAsync(ct).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            results.Add(new WorkaroundResult
            {
                Name = "win32-keep-alive",
                Applied = false,
                Detail = $"error: {ex.Message}",
            });
        }
        return results;
    }

    private async Task<WorkaroundResult> ApplyWin32KeepAliveLoaderAsync(CancellationToken ct = default)
    {
        // Find installed copilot version. Cheapest path: spawn `copilot --version`
        // and parse. We don't reuse UpdateCheckService because that does a full
        // network update; this should be local-only and fast.
        var version = await TryGetCopilotVersionAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(version))
            return new WorkaroundResult { Name = "win32-keep-alive", Applied = false, Detail = "copilot version not detectable" };

        var win32Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "copilot", "pkg", "universal", version!, "native", "win32");
        if (!Directory.Exists(win32Dir))
            return new WorkaroundResult { Name = "win32-keep-alive", Applied = false, Detail = $"version dir missing: {win32Dir}" };

        var loaderPath = Path.Combine(win32Dir, "index.js");
        if (File.Exists(loaderPath))
            return new WorkaroundResult { Name = "win32-keep-alive", Applied = false, Detail = "already present" };

        // Sanity check: only patch if the .node binary is actually there.
        // No point creating an index.js that points at a missing dep.
        var nodeBinary = Path.Combine(win32Dir, "win32-native.win32-x64-msvc.node");
        if (!File.Exists(nodeBinary))
            return new WorkaroundResult { Name = "win32-keep-alive", Applied = false, Detail = "win32-native .node binary not found; nothing to patch" };

        File.WriteAllText(loaderPath, Win32LoaderStub);
        return new WorkaroundResult { Name = "win32-keep-alive", Applied = true, Detail = $"wrote loader to {loaderPath}" };
    }

    private static async Task<string?> TryGetCopilotVersionAsync(CancellationToken ct = default)
    {
        try
        {
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
            psi.ArgumentList.Add("--version");

            using var proc = Process.Start(psi);
            if (proc is null) return null;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            try
            {
                var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
                var stderrTask = proc.StandardError.ReadToEndAsync(cts.Token);
                await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                var stdout = (await stdoutTask.ConfigureAwait(false)).Trim();
                _ = await stderrTask.ConfigureAwait(false);

                var m = Regex.Match(stdout, @"(?<v>\d+\.\d+\.\d+)");
                return m.Success ? m.Groups["v"].Value : null;
            }
            catch (OperationCanceledException)
            {
                TryKill(proc);
                return null;
            }
            catch
            {
                TryKill(proc);
                return null;
            }
        }
        catch
        {
            return null;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort only.
        }
    }
}
