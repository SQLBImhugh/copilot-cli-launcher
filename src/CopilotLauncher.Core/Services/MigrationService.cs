using System.Text.Json;
using CopilotLauncher.Models;

namespace CopilotLauncher.Services;

public sealed class LegacyDetectionResult
{
    /// <summary>True if the legacy %USERPROFILE%\copilot-launcher\config.json exists.</summary>
    public required bool Found { get; init; }
    public string? LegacyConfigPath { get; init; }
    public string? LegacyAgentsMdPath { get; init; }
    public string? LegacyProjectName { get; init; }
    public string? LegacyStateDir { get; init; }
}

public interface IMigrationService
{
    /// <summary>Detect a legacy PowerShell-launcher install at the default path.</summary>
    LegacyDetectionResult Detect();

    /// <summary>True if the user has already accepted or declined the migration.</summary>
    bool MigrationCompleted { get; }

    /// <summary>Mark migration as completed (whether the user imported or skipped).</summary>
    void MarkCompleted();

    /// <summary>
    /// Best-effort import: copies the legacy agents.md into the new app data
    /// folder if not already present, and seeds default settings from the
    /// legacy config.json. Idempotent. Returns a short status string.
    /// </summary>
    string Import(LegacyDetectionResult result);
}

public sealed class MigrationService : IMigrationService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ISettingsService _settings;
    private readonly string _legacyRoot;

    public MigrationService(ISettingsService settings, string? legacyRootOverride = null)
    {
        _settings = settings;
        _legacyRoot = legacyRootOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "copilot-launcher");
    }

    public bool MigrationCompleted => _settings.Current.MigrationCompleted;

    public void MarkCompleted()
    {
        _settings.Current.MigrationCompleted = true;
        _settings.Save();
    }

    public LegacyDetectionResult Detect()
    {
        var configPath = Path.Combine(_legacyRoot, "config.json");
        var agentsPath = Path.Combine(_legacyRoot, "agents.md");

        if (!File.Exists(configPath))
        {
            return new LegacyDetectionResult { Found = false };
        }

        string? projectName = null;
        string? stateDir = null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
            if (doc.RootElement.TryGetProperty("projectName", out var p) && p.ValueKind == JsonValueKind.String)
                projectName = p.GetString();
            if (doc.RootElement.TryGetProperty("stateDir", out var s) && s.ValueKind == JsonValueKind.String)
                stateDir = s.GetString();
        }
        catch
        {
            // Tolerate parse errors; just signal "found" with what we have.
        }

        return new LegacyDetectionResult
        {
            Found = true,
            LegacyConfigPath = configPath,
            LegacyAgentsMdPath = File.Exists(agentsPath) ? agentsPath : null,
            LegacyProjectName = projectName,
            LegacyStateDir = stateDir,
        };
    }

    public string Import(LegacyDetectionResult result)
    {
        if (!result.Found || result.LegacyConfigPath is null)
            return "Nothing to import.";

        var actions = new List<string>();
        try
        {
            // Copy agents.md into the new app data folder if user hasn't set
            // a custom path AND we don't already have one.
            if (result.LegacyAgentsMdPath is not null)
            {
                var dest = Path.Combine(_settings.AppDataDirectory, "agents.md");
                if (!File.Exists(dest))
                {
                    File.Copy(result.LegacyAgentsMdPath, dest);
                    actions.Add($"copied agents.md to {dest}");
                }
                if (string.IsNullOrEmpty(_settings.Current.Briefings.AgentsContextFilePath))
                {
                    _settings.Current.Briefings.AgentsContextFilePath = dest;
                    actions.Add("set Briefings.AgentsContextFilePath");
                }
            }

            // Map legacy fields where they have natural equivalents. Most legacy
            // settings are about a single project, while 2.0 is multi-project,
            // so we don't auto-create a Shortcut — the user can do that easily
            // from the Sessions tab once they've migrated.
            if (!string.IsNullOrEmpty(result.LegacyProjectName))
            {
                actions.Add($"detected legacy project name '{result.LegacyProjectName}' (no auto-action — create a Shortcut from the Sessions tab if desired)");
            }

            MarkCompleted();
            _settings.Save();

            return actions.Count == 0
                ? "Nothing to copy; marked migration as complete."
                : "Imported: " + string.Join("; ", actions);
        }
        catch (Exception ex)
        {
            return $"Import failed: {ex.Message}";
        }
    }
}
