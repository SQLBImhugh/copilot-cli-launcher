using System.Diagnostics;
using CopilotLauncher.Helpers;
using CopilotLauncher.Models;

namespace CopilotLauncher.Services;

/// <summary>
/// One launch invocation: terminal + workdir + copilot + flags + resume target.
/// </summary>
public sealed class LaunchRequest
{
    public required string WorkingDirectory { get; init; }

    /// <summary>Empty/null means no --resume flag (fresh session).</summary>
    public string? ResumeTarget { get; init; }

    public bool EnableAISummary { get; init; }
    public bool EnableAllowAll { get; init; }
    public string? ExtraCopilotArgs { get; init; }

    /// <summary>
    /// Terminal to wrap copilot in. Null = no terminal wrapper, spawn copilot
    /// directly attached to the parent process console (rare; mostly for tests).
    /// </summary>
    public TerminalProfile? Terminal { get; init; }
}

/// <summary>
/// Result of building a launch command. Returned by <see cref="ILaunchService.Build"/>.
/// </summary>
public sealed class LaunchCommand
{
    public required string FileName { get; init; }
    public required IReadOnlyList<string> ArgumentList { get; init; }
    public required string WorkingDirectory { get; init; }

    /// <summary>The exact Arguments string written to a .lnk if exported. Quote-escaped per Windows rules.</summary>
    public string ArgumentString => ArgQuoter.Format(ArgumentList);
}

public interface ILaunchService
{
    /// <summary>Build the spawn descriptor without launching. Useful for previews + .lnk export.</summary>
    LaunchCommand Build(LaunchRequest request);

    /// <summary>Spawn the process. Returns started process or throws on failure.</summary>
    Process Spawn(LaunchRequest request);
}

public sealed class LaunchService : ILaunchService
{
    private readonly Func<string, string?> _resolveOnPath;

    public LaunchService()
        : this(TerminalDiscoveryService.ResolveOnPath) { }

    /// <summary>Test-only ctor.</summary>
    internal LaunchService(Func<string, string?> resolveOnPath)
    {
        _resolveOnPath = resolveOnPath;
    }

    public LaunchCommand Build(LaunchRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.WorkingDirectory))
            throw new ArgumentException("WorkingDirectory is required.", nameof(request));

        var copilot = ProcessUtil.Resolve(_resolveOnPath)
            ?? throw new InvalidOperationException(
                "copilot CLI not found on PATH. Install it via `npm install -g @github/copilot` or set the path in Settings.");

        // Build the copilot argument list (always passed as the inner command).
        var copilotArgs = new List<string>(copilot.PrefixArgs);
        // PrefixArgs already contains pwsh -NoProfile -File <copilot.ps1> when .ps1 fallback is used;
        // append the user-facing copilot flags after.
        if (request.EnableAISummary) { /* AI summary is a launcher-side concern, not a copilot flag — handled elsewhere */ }
        if (request.EnableAllowAll) copilotArgs.Add("--allow-all");
        if (!string.IsNullOrWhiteSpace(request.ResumeTarget)) copilotArgs.Add($"--resume={request.ResumeTarget}");
        if (!string.IsNullOrWhiteSpace(request.ExtraCopilotArgs))
            copilotArgs.AddRange(ArgQuoter.Split(request.ExtraCopilotArgs));

        // Wrap in terminal (or run direct).
        if (request.Terminal is null)
        {
            return new LaunchCommand
            {
                FileName = copilot.FileName,
                ArgumentList = copilotArgs,
                WorkingDirectory = request.WorkingDirectory,
            };
        }

        return WrapInTerminal(request.Terminal, copilot.FileName, copilotArgs, request.WorkingDirectory);
    }

    public Process Spawn(LaunchRequest request)
    {
        var validatedWorkingDirectory = PathValidator.ValidateWorkingDirectory(request.WorkingDirectory);
        if (validatedWorkingDirectory is null)
            throw new InvalidOperationException($"Working directory does not exist or is invalid: {request.WorkingDirectory}");

        var cmd = Build(new LaunchRequest
        {
            WorkingDirectory = validatedWorkingDirectory,
            ResumeTarget = request.ResumeTarget,
            EnableAISummary = request.EnableAISummary,
            EnableAllowAll = request.EnableAllowAll,
            ExtraCopilotArgs = request.ExtraCopilotArgs,
            Terminal = request.Terminal,
        });
        var psi = new ProcessStartInfo
        {
            FileName = cmd.FileName,
            WorkingDirectory = cmd.WorkingDirectory,
            UseShellExecute = false,
        };
        foreach (var a in cmd.ArgumentList)
            psi.ArgumentList.Add(a);
        return Process.Start(psi)
            ?? throw new InvalidOperationException("Process.Start returned null — failed to launch.");
    }

    private static LaunchCommand WrapInTerminal(
        TerminalProfile terminal,
        string copilotExe,
        IReadOnlyList<string> copilotArgs,
        string workdir)
    {
        switch (terminal.Id)
        {
            case "wt":
                {
                    // wt.exe -w 0 -d "<workdir>" "<copilot>" <copilot args>
                    var args = new List<string> { "-w", "0", "-d", workdir, copilotExe };
                    args.AddRange(copilotArgs);
                    return new LaunchCommand
                    {
                        FileName = terminal.ExecutablePath,
                        ArgumentList = args,
                        WorkingDirectory = workdir,
                    };
                }

            case "pwsh":
            case "powershell":
                {
                    // Use Start-Process with -ArgumentList for safe arg passing.
                    // Building a -Command string with `&` invocation requires escaping
                    // every single quote, dollar sign, semicolon, and backtick that may
                    // appear in copilot args — error-prone and a likely injection vector.
                    // Start-Process accepts an array literal so each arg is passed
                    // verbatim to the child process without any extra parsing.
                    var psArgArray = string.Join(",", copilotArgs.Select(SingleQuoteForPowerShell));
                    var inner = psArgArray.Length > 0
                        ? $"Start-Process -FilePath {SingleQuoteForPowerShell(copilotExe)} -ArgumentList @({psArgArray}) -NoNewWindow -Wait"
                        : $"Start-Process -FilePath {SingleQuoteForPowerShell(copilotExe)} -NoNewWindow -Wait";
                    var args = new List<string>
                    {
                        "-NoExit", "-NoLogo", "-ExecutionPolicy", "Bypass",
                        "-Command", inner,
                    };
                    return new LaunchCommand
                    {
                        FileName = terminal.ExecutablePath,
                        ArgumentList = args,
                        WorkingDirectory = workdir,
                    };
                }

            case "cmd":
                {
                    // cmd /K "<copilot> <args>"
                    var inner = $"\"{copilotExe}\" {ArgQuoter.Format(copilotArgs)}";
                    return new LaunchCommand
                    {
                        FileName = terminal.ExecutablePath,
                        ArgumentList = new[] { "/K", inner },
                        WorkingDirectory = workdir,
                    };
                }

            default:
                throw new NotSupportedException(
                    $"Terminal id '{terminal.Id}' is not supported yet. Custom terminal templates land in Phase 2.");
        }
    }

    /// <summary>
    /// Wrap a value as a PowerShell single-quoted literal: doubles embedded
    /// single quotes per PowerShell's literal-string syntax. Inside a single-
    /// quoted PowerShell string, NO interpolation or special characters are
    /// processed (unlike double-quoted strings where $, ", and ` are special).
    /// This is the only safe way to pass arbitrary user data through a
    /// PowerShell -Command string.
    /// </summary>
    internal static string SingleQuoteForPowerShell(string value) =>
        "'" + (value ?? string.Empty).Replace("'", "''") + "'";
}
