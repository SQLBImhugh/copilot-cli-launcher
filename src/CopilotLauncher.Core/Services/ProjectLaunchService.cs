using CopilotLauncher.Helpers;
using CopilotLauncher.Models;

namespace CopilotLauncher.Services;

/// <summary>Result of a project-aware launch, for status messaging.</summary>
public sealed class ProjectLaunchResult
{
    public bool Success { get; init; }
    public ProjectProfile? Project { get; init; }
    public string? TerminalName { get; init; }
    public bool RepoConfigSynced { get; init; }
    public string? Error { get; init; }

    /// <summary>"in Windows Terminal [MyRepo]. (repo plugin config synced)"</summary>
    public string Describe()
    {
        if (!Success) return Error ?? "Launch failed.";
        var project = Project is null ? string.Empty : $" [{Project.Label}]";
        var repo = RepoConfigSynced ? " (repo plugin config synced)" : string.Empty;
        return $"in {TerminalName ?? "direct"}{project}.{repo}";
    }
}

/// <summary>
/// Launches a Copilot session with the settings of whichever
/// <see cref="ProjectProfile"/> governs the target directory. Shared by the
/// Sessions tab (Resume / new session here) and the Projects tab so a project
/// always starts identically no matter which button was pressed.
/// </summary>
public interface IProjectLaunchService
{
    /// <summary>Effective settings for a directory, without launching.</summary>
    ResolvedLaunchProfile Resolve(string? directory);

    /// <summary>
    /// Start (or resume) a session in <paramref name="directory"/>, applying the resolved
    /// project profile. Pass null <paramref name="resumeTarget"/> for a fresh session.
    /// Never throws — failures come back on the result.
    /// </summary>
    ProjectLaunchResult Launch(string directory, string? resumeTarget);
}

public sealed class ProjectLaunchService : IProjectLaunchService
{
    private readonly ILaunchService _launch;
    private readonly ITerminalDiscoveryService _terminals;
    private readonly ISettingsService _settings;
    private readonly IProjectsService? _projects;
    private readonly IRepoConfigService? _repoConfig;
    private readonly ISessionCapabilityService? _capabilities;
    private readonly IExtensionPermissionService? _extPerms;
    private readonly IAfterLaunchAction _afterLaunch;

    public ProjectLaunchService(
        ILaunchService launch,
        ITerminalDiscoveryService terminals,
        ISettingsService settings,
        IProjectsService? projects = null,
        IRepoConfigService? repoConfig = null,
        ISessionCapabilityService? capabilities = null,
        IExtensionPermissionService? extPerms = null,
        IAfterLaunchAction? afterLaunch = null)
    {
        _launch = launch;
        _terminals = terminals;
        _settings = settings;
        _projects = projects;
        _repoConfig = repoConfig;
        _capabilities = capabilities;
        _extPerms = extPerms;
        _afterLaunch = afterLaunch ?? new NoopAfterLaunchAction();
    }

    public ResolvedLaunchProfile Resolve(string? directory) =>
        _projects?.Resolve(directory, _settings.Current)
        ?? ProjectMatcher.Resolve(null, _settings.Current);

    public ProjectLaunchResult Launch(string directory, string? resumeTarget)
    {
        try
        {
            var profile = Resolve(directory);
            var terminal = ResolveTerminal(profile.TerminalOverride);

            if (profile.PreApproveExtensions)
                _extPerms?.EnsureExtensionGrants(directory);

            var synced = SyncRepoConfig(profile.Project);

            _launch.Spawn(new LaunchRequest
            {
                WorkingDirectory = directory,
                ResumeTarget = resumeTarget,
                EnableAllowAll = profile.EnableAllowAll,
                ExtraCopilotArgs = profile.ExtraCopilotArgs,
                Capabilities = profile.Capabilities,
                Terminal = terminal,
            });

            _afterLaunch.Apply(_settings.Current.LauncherBehavior.AfterLaunch);

            return new ProjectLaunchResult
            {
                Success = true,
                Project = profile.Project,
                TerminalName = terminal?.DisplayName,
                RepoConfigSynced = synced,
            };
        }
        catch (Exception ex)
        {
            return new ProjectLaunchResult { Success = false, Error = ex.Message };
        }
    }

    /// <summary>Mirror the project's plugin allowlist into
    /// <c>.github/copilot/settings.json</c> when the project opted in. Best-effort:
    /// a failure never blocks the launch. Uses the synchronous plugin read rather than
    /// <c>DiscoverAsync</c> so a launch never stalls on a CLI shell-out.</summary>
    private bool SyncRepoConfig(ProjectProfile? project)
    {
        if (project is null || !project.SyncRepoConfigOnLaunch) return false;
        if (project.RepoEnabledPlugins is null) return false;
        if (_repoConfig is null || _capabilities is null) return false;

        try
        {
            var plugins = _capabilities.GetInstalledPlugins();
            if (plugins.Count == 0) return false;
            return _repoConfig.WriteEnabledPlugins(project.Path, plugins, project.RepoEnabledPlugins);
        }
        catch
        {
            return false;
        }
    }

    private TerminalProfile? ResolveTerminal(string? overrideId)
    {
        var discovered = _terminals.Discovered;
        if (discovered.Count == 0) return null;

        var pref = !string.IsNullOrWhiteSpace(overrideId)
            ? overrideId
            : _settings.Current.Terminal.DefaultTerminal;

        if (!string.IsNullOrEmpty(pref) && pref != "auto")
        {
            var match = discovered.FirstOrDefault(t => t.Id == pref);
            if (match is not null) return match;
        }
        // Auto-pick: prefer wt > pwsh > powershell > cmd.
        return discovered.OrderBy(t => t.Id switch
        {
            "wt" => 0,
            "pwsh" => 1,
            "powershell" => 2,
            "cmd" => 3,
            _ => 4
        }).First();
    }
}
