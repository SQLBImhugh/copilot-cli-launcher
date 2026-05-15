using System.Text.Json;
using CopilotLauncher.Models;

namespace CopilotLauncher.Services;

public interface IShortcutsService
{
    string FilePath { get; }
    IReadOnlyList<Shortcut> All { get; }
    void Reload();
    void Add(Shortcut launch);
    void Update(Shortcut launch);
    void Remove(string id);
    Shortcut? GetById(string id);
}

public sealed class ShortcutsService : IShortcutsService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public string FilePath { get; }
    private List<Shortcut> _items = new();
    public IReadOnlyList<Shortcut> All => _items;

    public ShortcutsService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CopilotLauncher", "shortcuts.json")) { }

    /// <summary>Test-only ctor.</summary>
    internal ShortcutsService(string filePath)
    {
        FilePath = filePath;
        Reload();
    }

    public void Reload()
    {
        // One-time migration: the file used to be called launches.json before
        // we renamed the entity from "Launch" to "Shortcut". If the new path
        // doesn't exist but the legacy one does, rename it in place so the
        // user keeps their saved entries across the rename.
        if (!File.Exists(FilePath))
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                var legacyPath = Path.Combine(dir, "launches.json");
                if (File.Exists(legacyPath))
                {
                    try { File.Move(legacyPath, FilePath); }
                    catch { /* fall through to empty-list path */ }
                }
            }
        }

        if (!File.Exists(FilePath))
        {
            _items = new List<Shortcut>();
            return;
        }

        string json;
        try
        {
            json = File.ReadAllText(FilePath);
        }
        catch (IOException)
        {
            // File is locked (e.g., user has it open in another app) or other IO
            // issue. Preserve current in-memory data — don't silently reset
            // _items to empty, because a subsequent Save would overwrite the
            // real data on disk with nothing. Surface to the caller so the UI
            // can show an error toast.
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            throw;
        }

        try
        {
            _items = JsonSerializer.Deserialize<List<Shortcut>>(json, JsonOpts) ?? new List<Shortcut>();
        }
        catch (JsonException)
        {
            // Truly corrupt JSON: back it up + reset to empty. Distinct from
            // the IO-exception path above; here we know the on-disk content
            // is unrecoverable, so we save what's there as evidence and
            // continue with a fresh slate.
            try
            {
                var backup = FilePath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
                File.Copy(FilePath, backup, overwrite: true);
            }
            catch
            {
                // Best effort backup; if even that fails, proceed with reset.
            }
            _items = new List<Shortcut>();
        }
    }

    public void Add(Shortcut launch)
    {
        _items.Add(launch);
        SaveAtomic();
    }

    public void Update(Shortcut launch)
    {
        var idx = _items.FindIndex(x => x.Id == launch.Id);
        if (idx < 0) throw new KeyNotFoundException($"No launch with id={launch.Id}");
        launch.UpdatedAt = DateTime.UtcNow;
        _items[idx] = launch;
        SaveAtomic();
    }

    public void Remove(string id)
    {
        _items.RemoveAll(x => x.Id == id);
        SaveAtomic();
    }

    public Shortcut? GetById(string id) => _items.FirstOrDefault(x => x.Id == id);

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
