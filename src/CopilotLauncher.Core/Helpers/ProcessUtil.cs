namespace CopilotLauncher.Helpers;

/// <summary>
/// Resolves the correct executable to invoke for the `copilot` CLI when
/// spawning via <c>System.Diagnostics.Process</c>.
///
/// Background: npm on Windows installs `copilot` as three sibling shims
/// (extension-less, .cmd, .ps1). PATH lookup may resolve any of them, but
/// <c>Process.Start</c> with <c>UseShellExecute=false</c> cannot run a
/// .ps1 directly (not a valid PE). It also fails on the bare `copilot`
/// no-extension shim because the .NET PATH probe only matches PATHEXT.
///
/// Direct port of <c>Resolve-CopilotProcessTarget</c> from the legacy PS
/// launcher. See <c>legacy/Launch-Copilot.ps1</c> for the original.
/// </summary>
public static class ProcessUtil
{
    public sealed class CopilotProcessTarget
    {
        public required string FileName { get; init; }
        public IReadOnlyList<string> PrefixArgs { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// Returns the executable + any prefix arguments needed to invoke copilot
    /// via Process.Start with UseShellExecute=false. Returns null if no
    /// copilot shim is found anywhere on PATH.
    /// </summary>
    /// <param name="resolveOnPath">PATH probe (e.g. <see cref="Services.TerminalDiscoveryService.ResolveOnPath"/>).</param>
    public static CopilotProcessTarget? Resolve(Func<string, string?> resolveOnPath)
    {
        // 1. Prefer .cmd or .exe (npm always ships .cmd alongside .ps1).
        var cmd = resolveOnPath("copilot.cmd");
        if (cmd is not null)
            return new CopilotProcessTarget { FileName = cmd };

        var exe = resolveOnPath("copilot.exe");
        if (exe is not null)
            return new CopilotProcessTarget { FileName = exe };

        // 2. Fall back to .ps1 invoked via pwsh -File.
        var ps1 = resolveOnPath("copilot.ps1");
        if (ps1 is not null)
        {
            var pwsh = resolveOnPath("pwsh.exe") ?? resolveOnPath("powershell.exe");
            if (pwsh is not null)
            {
                return new CopilotProcessTarget
                {
                    FileName = pwsh,
                    PrefixArgs = new[] { "-NoProfile", "-File", ps1 }
                };
            }
        }

        return null;
    }
}
