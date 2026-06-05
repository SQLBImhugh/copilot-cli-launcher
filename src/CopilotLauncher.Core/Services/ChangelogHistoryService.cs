using System.Text.Json;
using CopilotLauncher.Models;

namespace CopilotLauncher.Services;

public interface IChangelogHistoryService
{
    string FilePath { get; }

    /// <summary>Latest first. Capped at 50 entries (oldest are rotated out).</summary>
    IReadOnlyList<ChangelogEntry> All { get; }

    void Reload();

    /// <summary>Append a new changelog entry. Persists immediately. Caps history at 50.</summary>
    void Add(ChangelogEntry entry);

    /// <summary>Wipe the on-disk history. Used by the Changelog page "Clear changelogs" action.</summary>
    void Clear();
}

/// <summary>
/// Mirrors <see cref="BriefingHistoryService"/> but persists raw changelog
/// entries (release notes + `copilot update` stdout) separately from AI
/// summaries. Introduced in v0.2.0 alongside the Changelog/Briefings split.
/// </summary>
public sealed class ChangelogHistoryService : IChangelogHistoryService
{
    private const int MaxHistoryEntries = 50;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public string FilePath { get; }

    private List<ChangelogEntry> _items = new();
    public IReadOnlyList<ChangelogEntry> All => _items;

    public ChangelogHistoryService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CopilotLauncher", "changelogs.json"))
    { }

    /// <summary>Test-only ctor.</summary>
    internal ChangelogHistoryService(string filePath)
    {
        FilePath = filePath;
        Reload();
    }

    public void Reload()
    {
        if (!File.Exists(FilePath)) { _items = new List<ChangelogEntry>(); return; }
        string json;
        try { json = File.ReadAllText(FilePath); }
        catch (IOException) { throw; }
        catch (UnauthorizedAccessException) { throw; }

        try
        {
            _items = JsonSerializer.Deserialize<List<ChangelogEntry>>(json, JsonOpts) ?? new();
        }
        catch (JsonException)
        {
            // Corrupt — back up + reset. Same convention as BriefingHistoryService.
            try
            {
                var bak = FilePath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
                File.Copy(FilePath, bak, overwrite: true);
            }
            catch { }
            _items = new();
        }
    }

    public void Add(ChangelogEntry entry)
    {
        _items.Add(entry);
        _items = _items
            .OrderByDescending(e => e.Timestamp)
            .Take(MaxHistoryEntries)
            .ToList();
        SaveAtomic();
    }

    public void Clear()
    {
        _items.Clear();
        SaveAtomic();
    }

    private void SaveAtomic()
    {
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(_items, JsonOpts);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, json);
        if (File.Exists(FilePath))
            File.Replace(tmp, FilePath, FilePath + ".bak");
        else
            File.Move(tmp, FilePath);
    }
}
