using System.Diagnostics;
using CopilotLauncher.Models;

namespace CopilotLauncher.Services;

public interface ITerminalDiscoveryService
{
    /// <summary>
    /// Enumerate terminals available on this machine. Always returns at least
    /// one entry — at minimum the system PowerShell — so the Settings dropdown
    /// is never empty. Call <see cref="Refresh"/> to re-probe after changes.
    /// </summary>
    IReadOnlyList<TerminalProfile> Discovered { get; }

    /// <summary>Re-scan PATH and well-known locations.</summary>
    void Refresh();
}

public sealed class TerminalDiscoveryService : ITerminalDiscoveryService
{
    private List<TerminalProfile> _profiles = new();
    public IReadOnlyList<TerminalProfile> Discovered => _profiles;

    /// <summary>Optional path lookup override for testing.</summary>
    private readonly Func<string, string?> _resolve;

    public TerminalDiscoveryService()
        : this(ResolveOnPath) { }

    /// <summary>Test-only ctor.</summary>
    internal TerminalDiscoveryService(Func<string, string?> resolveOnPath)
    {
        _resolve = resolveOnPath;
        Refresh();
    }

    public void Refresh()
    {
        var list = new List<TerminalProfile>();

        // 1. Windows Terminal — preferred when present.
        var wt = _resolve("wt.exe");
        if (wt is not null)
        {
            list.Add(new TerminalProfile
            {
                Id = "wt",
                DisplayName = "Windows Terminal",
                ExecutablePath = wt,
                SupportsTabs = true,
                SupportsWorkingDirectoryFlag = true,
            });
        }

        // 2. PowerShell 7+ (pwsh).
        var pwsh = _resolve("pwsh.exe");
        if (pwsh is not null)
        {
            list.Add(new TerminalProfile
            {
                Id = "pwsh",
                DisplayName = "PowerShell 7",
                ExecutablePath = pwsh,
                SupportsTabs = false,
                SupportsWorkingDirectoryFlag = false,
            });
        }

        // 3. Windows PowerShell 5.1.
        var ps = _resolve("powershell.exe");
        if (ps is not null)
        {
            list.Add(new TerminalProfile
            {
                Id = "powershell",
                DisplayName = "Windows PowerShell 5.1",
                ExecutablePath = ps,
                SupportsTabs = false,
                SupportsWorkingDirectoryFlag = false,
            });
        }

        // 4. cmd.exe — guaranteed on every Windows.
        var cmd = _resolve("cmd.exe");
        if (cmd is not null)
        {
            list.Add(new TerminalProfile
            {
                Id = "cmd",
                DisplayName = "Command Prompt",
                ExecutablePath = cmd,
                SupportsTabs = false,
                SupportsWorkingDirectoryFlag = false,
            });
        }

        _profiles = list;
    }

    /// <summary>
    /// Cross-version equivalent of `Get-Command &lt;name&gt;`. Walks PATH and
    /// returns the first .exe match; null if not found. Resilient to malformed
    /// PATH entries.
    /// </summary>
    public static string? ResolveOnPath(string exeName)
    {
        if (string.IsNullOrWhiteSpace(exeName)) return null;

        // Direct path? Use as-is.
        if (Path.IsPathRooted(exeName))
            return File.Exists(exeName) ? exeName : null;

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var separator = OperatingSystem.IsWindows() ? ';' : ':';

        foreach (var raw in pathEnv.Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            string dir;
            try
            {
                // Strip surrounding double-quotes that occasionally appear in
                // PATH entries (some installers add `"C:\Program Files\X"`
                // verbatim). We don't do quote-aware splitting because true
                // quoted-segment PATH entries are extremely rare on Windows
                // and not worth the complexity for a UI service.
                dir = raw.Trim().Trim('"');
                if (dir.Length == 0) continue;
            }
            catch { continue; }

            string candidate;
            try
            {
                candidate = Path.Combine(dir, exeName);
            }
            catch
            {
                continue;
            }

            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }
}
