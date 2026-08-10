using System.Text;

namespace CopilotLauncher.Services;

/// <summary>
/// Reads and writes the AI-briefing "project context" file (an AGENTS.md-style
/// markdown file). Its contents are appended to every briefing prompt as the
/// "Repository context:" section by <see cref="AISummaryPromptBuilder"/>.
/// </summary>
/// <remarks>
/// This is the second half of briefing customization: <c>PromptInstructions</c>
/// is the *ask*, this file is the *context* (what the project is, what the user
/// cares about). Both are surfaced by the Changelog tab's "Customize
/// instructions…" editor. Kept in Core so the file handling is unit-testable.
/// </remarks>
public interface IBriefingContextService
{
    /// <summary>Effective path of the context file — the configured
    /// <c>Briefings.AgentsContextFilePath</c>, or a default under the app-data
    /// folder when unset. Never null.</summary>
    string ResolvePath();

    /// <summary>Read the context file. Returns empty string when it doesn't
    /// exist or can't be read (never throws).</summary>
    Task<string> ReadAsync(CancellationToken ct = default);

    /// <summary>
    /// Write the context file atomically (temp + replace), backing up any prior
    /// contents once per save as <c>&lt;name&gt;.bak-yyyyMMdd-HHmmss</c>.
    /// Also persists the resolved path into settings when it wasn't set, so the
    /// prompt builder picks the file up. Throws on I/O failure so the caller can
    /// surface a real error.
    /// </summary>
    Task WriteAsync(string content, CancellationToken ct = default);
}

public sealed class BriefingContextService : IBriefingContextService
{
    /// <summary>File name used when the user has no context path configured.</summary>
    internal const string DefaultFileName = "AGENTS.md";

    private readonly ISettingsService _settings;

    public BriefingContextService(ISettingsService settings)
    {
        _settings = settings;
    }

    public string ResolvePath()
    {
        var configured = _settings.Current.Briefings.AgentsContextFilePath;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            try { return Path.GetFullPath(configured); }
            catch { return configured; }
        }
        return Path.Combine(_settings.AppDataDirectory, DefaultFileName);
    }

    public async Task<string> ReadAsync(CancellationToken ct = default)
    {
        try
        {
            var path = ResolvePath();
            if (!File.Exists(path)) return string.Empty;
            return await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        }
        catch
        {
            // Unreadable context is not fatal — the editor just opens empty and
            // the briefing prompt omits the section.
            return string.Empty;
        }
    }

    public async Task WriteAsync(string content, CancellationToken ct = default)
    {
        var path = ResolvePath();
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Back up prior contents before overwriting — this file is hand-authored
        // and can represent a lot of user effort.
        if (File.Exists(path))
        {
            var backup = path + ".bak-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            try { if (!File.Exists(backup)) File.Copy(path, backup); }
            catch { /* best effort */ }
        }

        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, content ?? string.Empty, new UTF8Encoding(false), ct).ConfigureAwait(false);
        if (File.Exists(path))
            File.Replace(tmp, path, destinationBackupFileName: null);
        else
            File.Move(tmp, path);

        // If the user had no path configured we just created the default one —
        // persist it so AISummaryService actually reads it back.
        if (string.IsNullOrWhiteSpace(_settings.Current.Briefings.AgentsContextFilePath))
        {
            _settings.Current.Briefings.AgentsContextFilePath = path;
            try { _settings.Save(); }
            catch { /* the file itself is written; a failed settings save is recoverable */ }
        }
    }
}
