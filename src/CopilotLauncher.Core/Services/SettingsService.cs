using System.Text.Json;
using CopilotLauncher.Models;

namespace CopilotLauncher.Services;

public interface ISettingsService
{
    /// <summary>Root folder for app state: %LOCALAPPDATA%\CopilotLauncher.</summary>
    string AppDataDirectory { get; }

    /// <summary>Full path to settings.json under AppDataDirectory.</summary>
    string SettingsFilePath { get; }

    AppSettings Current { get; }

    /// <summary>Reload settings from disk (or create defaults if missing).</summary>
    void Load();

    /// <summary>Persist current settings atomically.</summary>
    void Save();
}

public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public string AppDataDirectory { get; }
    public string SettingsFilePath { get; }
    public AppSettings Current { get; private set; } = new();

    public SettingsService()
    {
        AppDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CopilotLauncher");
        Directory.CreateDirectory(AppDataDirectory);
        SettingsFilePath = Path.Combine(AppDataDirectory, "settings.json");
        Load();
    }

    public void Load()
    {
        if (!File.Exists(SettingsFilePath))
        {
            Current = new AppSettings();
            Save();
            return;
        }

        try
        {
            var json = File.ReadAllText(SettingsFilePath);
            var parsed = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts);
            Current = parsed ?? new AppSettings();
        }
        catch (Exception)
        {
            // Corrupt settings.json — back it up, start fresh. Log to file once
            // the logging service exists.
            var backup = SettingsFilePath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            File.Copy(SettingsFilePath, backup, overwrite: true);
            Current = new AppSettings();
            Save();
        }
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(Current, JsonOpts);
        // Atomic write: write temp + move.
        var tmp = SettingsFilePath + ".tmp";
        File.WriteAllText(tmp, json);
        if (File.Exists(SettingsFilePath))
        {
            File.Replace(tmp, SettingsFilePath, SettingsFilePath + ".bak");
        }
        else
        {
            File.Move(tmp, SettingsFilePath);
        }
    }
}
