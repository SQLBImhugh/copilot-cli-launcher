using System.Text.Json;
using CopilotLauncher.Models;

namespace CopilotLauncher.Services;

public interface IBriefingHistoryService
{
    string FilePath { get; }

    /// <summary>Latest first. Capped at 50 entries (oldest are rotated out).</summary>
    IReadOnlyList<BriefingEntry> All { get; }

    void Reload();

    /// <summary>Append a new briefing entry. Persists immediately. Caps history at 50.</summary>
    void Add(BriefingEntry entry);

    /// <summary>Wipe the on-disk history. Used by Settings "Clear briefing history" action.</summary>
    void Clear();
}

public sealed class BriefingHistoryService : IBriefingHistoryService
{
    private const int MaxHistoryEntries = 50;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public string FilePath { get; }

    private List<BriefingEntry> _items = new();
    public IReadOnlyList<BriefingEntry> All => _items;

    public BriefingHistoryService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CopilotLauncher", "briefings.json"))
    { }

    /// <summary>Test-only ctor.</summary>
    internal BriefingHistoryService(string filePath)
    {
        FilePath = filePath;
        Reload();
    }

    public void Reload()
    {
        if (!File.Exists(FilePath)) { _items = new List<BriefingEntry>(); return; }
        string json;
        try { json = File.ReadAllText(FilePath); }
        catch (IOException) { throw; }
        catch (UnauthorizedAccessException) { throw; }

        try
        {
            _items = JsonSerializer.Deserialize<List<BriefingEntry>>(json, JsonOpts) ?? new();
        }
        catch (JsonException)
        {
            // Corrupt — back up + reset.
            try
            {
                var bak = FilePath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
                File.Copy(FilePath, bak, overwrite: true);
            }
            catch { }
            _items = new();
        }
    }

    public void Add(BriefingEntry entry)
    {
        _items.Add(entry);
        // Sort newest-first then trim. Sorting before trimming guarantees we
        // keep the newest entries even if the file was loaded out of order.
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
