using System.Diagnostics;
using System.Text;
using CopilotLauncher.Helpers;

namespace CopilotLauncher.Services;

public sealed class AISummaryService : IAISummaryService
{
    private readonly ISettingsService _settings;
    private readonly Func<ProcessStartInfo, Process?> _spawn;

    public AISummaryService(ISettingsService settings)
        : this(settings, Process.Start)
    {
    }

    internal AISummaryService(ISettingsService settings, Func<ProcessStartInfo, Process?> spawn)
    {
        _settings = settings;
        _spawn = spawn;
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
            var copilot = ResolveCopilotTarget(prompt);
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

    private static ProcessUtil.CopilotProcessTarget? ResolveCopilotTarget(string prompt)
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

    private static async Task<string?> TryReadRepositoryContextAsync(string? path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        try
        {
            var text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            return text.Length <= AISummaryPromptBuilder.RepositoryContextLimit ? text : null;
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
    internal const int RepositoryContextLimit = 4000;
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
            sb.AppendLine();
            sb.AppendLine("Repository context:");
            sb.AppendLine(repoContext.Trim());
        }

        return sb.ToString().TrimEnd();
    }
}
