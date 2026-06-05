using System.Text.Json;
using CopilotLauncher.Models;

namespace CopilotLauncher.Services;

public interface IBriefingsSplitMigration
{
    /// <summary>
    /// Idempotent one-shot migration that splits pre-v0.2.0 briefings.json
    /// entries — which conflated AI summaries and raw changelogs in a single
    /// <see cref="BriefingEntry.Body"/> — into two separate files:
    /// <list type="bullet">
    ///   <item><c>changelogs.json</c> — the changelog half (release notes + `copilot update` stdout)</item>
    ///   <item><c>briefings.json</c> — the AI summary half only</item>
    /// </list>
    /// Skips entirely if <c>changelogs.json</c> already exists (already migrated).
    /// Backs up the original <c>briefings.json</c> as <c>briefings.json.v01-backup</c>
    /// before mutating. Returns a short status string for logging / surfacing.
    /// </summary>
    string Migrate();
}

/// <summary>
/// v0.1.x → v0.2.0 migration: split combined AI-summary+changelog briefings
/// into two parallel histories.
/// </summary>
/// <remarks>
/// Pre-v0.2.0 the Briefing tab wrote entries whose <see cref="BriefingEntry.Body"/>
/// followed the format <c>"## AI Summary\n\n{summary}\n\n---\n\n{changelog}"</c>
/// when AI was enabled, or just <c>{changelog}</c> when it was disabled. v0.2.0
/// keeps AI generation on-demand and surfaces the changelog independently, so
/// the file layout no longer matches: we need one list of AI briefings and one
/// list of changelogs. This service performs that split exactly once.
/// </remarks>
public sealed class BriefingsSplitMigration : IBriefingsSplitMigration
{
    private const string AISummaryHeader = "## AI Summary";
    private const string Separator = "\n\n---\n\n";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _briefingsPath;
    private readonly string _changelogsPath;

    public BriefingsSplitMigration(IBriefingHistoryService briefings, IChangelogHistoryService changelogs)
        : this(briefings.FilePath, changelogs.FilePath)
    { }

    /// <summary>Test-only ctor.</summary>
    internal BriefingsSplitMigration(string briefingsPath, string changelogsPath)
    {
        _briefingsPath = briefingsPath;
        _changelogsPath = changelogsPath;
    }

    public string Migrate()
    {
        if (File.Exists(_changelogsPath))
            return "Skipped: changelogs.json already exists.";
        if (!File.Exists(_briefingsPath))
            return "Skipped: no briefings.json to migrate.";

        List<BriefingEntry>? legacy;
        try
        {
            var json = File.ReadAllText(_briefingsPath);
            legacy = JsonSerializer.Deserialize<List<BriefingEntry>>(json, JsonOpts);
        }
        catch (Exception ex)
        {
            return $"Skipped: could not read briefings.json ({ex.Message}).";
        }
        if (legacy is null || legacy.Count == 0)
            return "Skipped: briefings.json is empty.";

        var newBriefings = new List<BriefingEntry>();
        var newChangelogs = new List<ChangelogEntry>();

        foreach (var entry in legacy)
        {
            var (aiBody, changelogBody) = SplitBody(entry.Body);

            if (!string.IsNullOrWhiteSpace(aiBody))
            {
                newBriefings.Add(new BriefingEntry
                {
                    Id = entry.Id,
                    Timestamp = entry.Timestamp,
                    FromVersion = entry.FromVersion,
                    ToVersion = entry.ToVersion,
                    Source = "ai-summary",
                    Body = aiBody,
                });
            }

            if (!string.IsNullOrWhiteSpace(changelogBody))
            {
                newChangelogs.Add(new ChangelogEntry
                {
                    Id = Guid.NewGuid().ToString(),
                    Timestamp = entry.Timestamp,
                    FromVersion = entry.FromVersion,
                    ToVersion = entry.ToVersion,
                    Source = "migrated",
                    Body = changelogBody,
                });
            }
        }

        // Back up original before overwriting. Use a .v01-backup suffix so it's
        // obvious what version of the schema it came from. Never overwrite an
        // existing backup — that would lose any prior v0.1.x state from a
        // previous (interrupted) run.
        var backupPath = _briefingsPath + ".v01-backup";
        if (!File.Exists(backupPath))
        {
            try { File.Copy(_briefingsPath, backupPath); }
            catch { /* best-effort */ }
        }

        WriteJsonAtomic(_changelogsPath, newChangelogs);
        WriteJsonAtomic(_briefingsPath, newBriefings);

        return $"Migrated {legacy.Count} legacy briefing(s) → {newBriefings.Count} AI briefing(s) + {newChangelogs.Count} changelog(s).";
    }

    /// <summary>
    /// Split a pre-v0.2.0 body into (aiSummary, changelog) halves.
    /// Returns (null, body) when no AI Summary heading is present
    /// (early v0.1.x entries before AI was wired up, or AISummaryOnBump=false).
    /// </summary>
    internal static (string? AiSummary, string? Changelog) SplitBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return (null, null);

        var trimmed = body.TrimStart();
        if (!trimmed.StartsWith(AISummaryHeader, StringComparison.Ordinal))
        {
            // No AI summary in this entry — everything is changelog content.
            return (null, body);
        }

        // Split on the FIRST "\n\n---\n\n" separator. Subsequent ones (e.g.
        // dividers inside the changelog body itself) must stay with the
        // changelog half.
        var sepIdx = body.IndexOf(Separator, StringComparison.Ordinal);
        if (sepIdx < 0)
        {
            // Malformed: header present but no separator. Treat everything
            // after the header as AI summary; no changelog half.
            var headerEnd = body.IndexOf(AISummaryHeader, StringComparison.Ordinal) + AISummaryHeader.Length;
            var aiOnly = body.Substring(headerEnd).TrimStart('\r', '\n').Trim();
            return (string.IsNullOrWhiteSpace(aiOnly) ? null : aiOnly, null);
        }

        var headerStart = body.IndexOf(AISummaryHeader, StringComparison.Ordinal);
        var aiHalfStart = headerStart + AISummaryHeader.Length;
        var aiHalf = body.Substring(aiHalfStart, sepIdx - aiHalfStart).TrimStart('\r', '\n').Trim();
        var changelogHalf = body.Substring(sepIdx + Separator.Length).Trim();

        return (
            string.IsNullOrWhiteSpace(aiHalf) ? null : aiHalf,
            string.IsNullOrWhiteSpace(changelogHalf) ? null : changelogHalf);
    }

    private static void WriteJsonAtomic<T>(string path, T value)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(value, JsonOpts);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        if (File.Exists(path))
            File.Replace(tmp, path, path + ".bak");
        else
            File.Move(tmp, path);
    }
}
