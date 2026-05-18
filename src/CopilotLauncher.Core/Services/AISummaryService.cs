using System.Diagnostics;
using System.Text;
using CopilotLauncher.Helpers;

namespace CopilotLauncher.Services;

public sealed class AISummaryService : IAISummaryService
{
    private readonly ISettingsService _settings;
    private readonly Func<ProcessStartInfo, Process?> _spawn;
    private readonly Func<string, bool> _sessionExistsByName;
    private readonly Func<string, ProcessUtil.CopilotProcessTarget?> _resolveCopilot;

    public AISummaryService(ISettingsService settings, ISessionDiscoveryService sessions)
        : this(
            settings,
            Process.Start,
            name => sessions.Enumerate().Any(s => string.Equals(s.Name, name, StringComparison.Ordinal)),
            DefaultResolveCopilotTarget)
    {
    }

    internal AISummaryService(
        ISettingsService settings,
        Func<ProcessStartInfo, Process?> spawn,
        Func<string, bool> sessionExistsByName,
        Func<string, ProcessUtil.CopilotProcessTarget?>? resolveCopilot = null)
    {
        _settings = settings;
        _spawn = spawn;
        _sessionExistsByName = sessionExistsByName;
        _resolveCopilot = resolveCopilot ?? DefaultResolveCopilotTarget;
    }

    /// <summary>Test-only ctor that defaults the session check to "no session
    /// exists" — preserves the pre-session-reuse behavior for older tests
    /// that don't care about <c>--name</c> / <c>--resume</c> args.</summary>
    internal AISummaryService(ISettingsService settings, Func<ProcessStartInfo, Process?> spawn)
        : this(settings, spawn, _ => false, null)
    {
    }

    public bool IsEnabled => _settings.Current.Briefings.AISummaryOnBump;

    public async Task<string?> GenerateAsync(
        string fromVersion,
        string toVersion,
        string changelogText,
        CancellationToken ct = default)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(changelogText))
            return null;

        try
        {
            if (ct.IsCancellationRequested)
                return null;

            var repoContext = await TryReadRepositoryContextAsync(
                _settings.Current.Briefings.AgentsContextFilePath,
                ct).ConfigureAwait(false);

            var prompt = AISummaryPromptBuilder.Build(fromVersion, toVersion, changelogText, repoContext);
            var copilot = _resolveCopilot(prompt);
            if (copilot is null)
                return null;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(60));

            var psi = new ProcessStartInfo
            {
                FileName = copilot.FileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (var arg in copilot.PrefixArgs)
                psi.ArgumentList.Add(arg);

            // Session-reuse flags (optional). When BriefingSessionName is set,
            // resume the existing named session if one exists so the AI has
            // cumulative context across version-bump briefings; otherwise
            // create it on this first run. Empty/null = stateless one-shot
            // (original behavior).
            var sessionName = _settings.Current.Briefings.BriefingSessionName;
            if (!string.IsNullOrWhiteSpace(sessionName))
            {
                if (_sessionExistsByName(sessionName))
                {
                    psi.ArgumentList.Add($"--resume={sessionName}");
                }
                else
                {
                    psi.ArgumentList.Add("--name");
                    psi.ArgumentList.Add(sessionName);
                }
            }

            psi.ArgumentList.Add("-p");
            psi.ArgumentList.Add(prompt);
            psi.ArgumentList.Add("--no-color");

            using var process = _spawn(psi);
            if (process is null)
                return null;

            try
            {
                var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
                var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                var stdout = await stdoutTask.ConfigureAwait(false);
                _ = await stderrTask.ConfigureAwait(false);

                var trimmed = stdout.Trim();
                return trimmed.Length == 0 ? null : trimmed;
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                return null;
            }
            catch
            {
                TryKill(process);
                return null;
            }
        }
        catch
        {
            return null;
        }
    }

    private static ProcessUtil.CopilotProcessTarget? DefaultResolveCopilotTarget(string prompt)
    {
        var primary = ProcessUtil.Resolve(TerminalDiscoveryService.ResolveOnPath);
        if (primary is null)
            return null;

        // Multiline prompts are brittle through the npm-generated .cmd shim on
        // Windows, so prefer the .exe/.ps1 route when we can.
        if (!primary.FileName.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            || (!prompt.Contains('\n') && !prompt.Contains('\r')))
        {
            return primary;
        }

        return ProcessUtil.Resolve(name =>
            string.Equals(name, "copilot.cmd", StringComparison.OrdinalIgnoreCase)
                ? null
                : TerminalDiscoveryService.ResolveOnPath(name))
            ?? primary;
    }

    internal static async Task<string?> TryReadRepositoryContextAsync(string? path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            var full = Path.GetFullPath(path);
            if (!File.Exists(full))
                return null;

            var ext = Path.GetExtension(full).ToLowerInvariant();
            if (ext is not ".md" and not ".txt")
                return null;

            // Return the raw text — oversize files are truncated (with marker)
            // by AISummaryPromptBuilder.Build rather than silently dropped here,
            // matching how the changelog is handled.
            return await File.ReadAllTextAsync(full, ct).ConfigureAwait(false);
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

internal static class AISummaryPromptBuilder
{
    internal const int ChangelogLimit = 6000;

    /// <summary>Max chars of the optional repository-context file (AGENTS.md
    /// or similar) embedded in the prompt. Premium copilot CLI models handle
    /// 100K+ tokens easily, so 20K chars (~5K tokens) leaves plenty of room
    /// for a real agents.md plus the changelog. Files larger than this are
    /// truncated with a marker rather than silently dropped.</summary>
    internal const int RepositoryContextLimit = 20000;
    private const string TruncationMarker = "...[truncated]";

    public static string Build(string fromVersion, string toVersion, string changelog, string? repoContext)
    {
        var normalizedChangelog = changelog?.Trim() ?? string.Empty;
        if (normalizedChangelog.Length > ChangelogLimit)
        {
            normalizedChangelog = normalizedChangelog[..Math.Max(0, ChangelogLimit - TruncationMarker.Length)]
                + TruncationMarker;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Summarize the most important user-facing changes between GitHub Copilot CLI versions {fromVersion} and {toVersion} in 4-6 concise bullets.");
        sb.AppendLine("Focus on practical impact, notable fixes, and anything a Copilot CLI Launcher user should notice.");
        sb.AppendLine();
        sb.AppendLine("Changelog:");
        sb.AppendLine(normalizedChangelog);

        if (!string.IsNullOrWhiteSpace(repoContext))
        {
            var normalizedRepoContext = repoContext.Trim();
            if (normalizedRepoContext.Length > RepositoryContextLimit)
            {
                normalizedRepoContext = normalizedRepoContext[..Math.Max(0, RepositoryContextLimit - TruncationMarker.Length)]
                    + TruncationMarker;
            }
            sb.AppendLine();
            sb.AppendLine("Repository context:");
            sb.AppendLine(normalizedRepoContext);
        }

        return sb.ToString().TrimEnd();
    }
}
