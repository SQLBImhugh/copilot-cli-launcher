using System.Diagnostics;
using System.Text;
using System.Text.Json;
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
        // v0.2.0: AI generation is on-demand only (Generate AI Briefing
        // button). The act of calling this method IS the user's opt-in;
        // we no longer gate on the legacy AISummaryOnBump setting. The
        // IsEnabled property is preserved for backward compatibility but
        // is no longer consulted on the generation path. The empty-input
        // check stays as a sanity guard against feeding the model nothing.
        if (string.IsNullOrWhiteSpace(changelogText))
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
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(120));

            var psi = new ProcessStartInfo
            {
                FileName = copilot.FileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
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

            // copilot 1.0.49 quirk: in non-interactive `-p` mode the model's
            // text reply is NEVER printed to stdout in text output mode —
            // only tool-invocation banners and the stats footer appear.
            // The reply is only emitted via JSONL when `--output-format json`
            // is set. `--allow-all-tools` is required for any non-trivial
            // briefing because the model must fetch release notes from the
            // web or `gh`. Without it, the model's tool calls are denied
            // ("Permission denied and could not request permission from
            // user") and it ends up producing no useful summary anyway.
            psi.ArgumentList.Add("--allow-all-tools");
            psi.ArgumentList.Add("--output-format");
            psi.ArgumentList.Add("json");
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

                var extracted = ExtractAssistantTextFromJsonl(stdout);
                return string.IsNullOrWhiteSpace(extracted) ? null : extracted;
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

    /// <summary>
    /// Parses copilot's <c>--output-format json</c> JSONL output and extracts
    /// the model's text reply. The model emits one or more
    /// <c>assistant.message</c> events; we concatenate the non-empty
    /// <c>data.content</c> fields in order (excluding tool-call-only turns,
    /// where content is empty).
    ///
    /// Public + internal for unit testing.
    /// </summary>
    internal static string ExtractAssistantTextFromJsonl(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return string.Empty;

        var sb = new StringBuilder();
        foreach (var rawLine in stdout.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0 || line[0] != '{') continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                if (!root.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String) continue;
                if (typeEl.GetString() != "assistant.message") continue;
                if (!root.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Object) continue;
                if (!dataEl.TryGetProperty("content", out var contentEl) || contentEl.ValueKind != JsonValueKind.String) continue;
                var content = contentEl.GetString();
                if (string.IsNullOrWhiteSpace(content)) continue;
                if (sb.Length > 0) sb.AppendLine();
                sb.Append(content);
            }
            catch (JsonException)
            {
                // Skip malformed lines (e.g. terminal control sequences).
            }
        }

        return sb.ToString().Trim();
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
        // Strong guard against session-memory contamination. The briefing
        // session is reused across version bumps for cumulative context, so
        // prior turns may include hallucinated context (e.g. the legacy
        // PowerShell launcher's Launch-Copilot.ps1 / Get-RemoteChangelogEntries
        // helpers, which the WinUI rewrite has not had since v0.1.0). Without
        // this clause, when the changelog is thin the model defaults to those
        // memorized hallucinations instead of saying "no notes available".
        sb.AppendLine("IMPORTANT INSTRUCTIONS:");
        sb.AppendLine("- Base your summary STRICTLY on the changelog text below. Do not invent items.");
        sb.AppendLine("- Do not reference any prior turns in this session, any PowerShell scripts (Launch-Copilot.ps1, Get-RemoteChangelogEntries, etc.), any wrapper, or any cache files on the Desktop — none of those exist in this product.");
        sb.AppendLine("- If the changelog below is empty or contains only updater status lines like \"No update needed\" / \"Checking for updates...\", respond with exactly: \"No release notes available for this transition.\" and nothing else.");
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
