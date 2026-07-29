using System.Text.Json;
using CopilotLauncher.Helpers;
using CopilotLauncher.Models;

namespace CopilotLauncher.Services;

public interface IProjectsService
{
    string FilePath { get; }
    IReadOnlyList<ProjectProfile> All { get; }
    void Reload();
    void Add(ProjectProfile project);
    void Update(ProjectProfile project);
    void Remove(string id);
    ProjectProfile? GetById(string id);

    /// <summary>The profile governing <paramref name="directory"/>, or null.</summary>
    ProjectProfile? Match(string? directory);

    /// <summary>Effective launch settings for <paramref name="directory"/>: the matching
    /// profile merged over the supplied global defaults.</summary>
    ResolvedLaunchProfile Resolve(string? directory, AppSettings settings);
}

/// <summary>
/// Persists per-directory launch profiles in <c>projects.json</c> next to
/// <c>shortcuts.json</c>, and resolves a working directory to its profile.
/// Mirrors <see cref="ShortcutsService"/>'s atomic-write + corrupt-backup
/// behavior so a bad file never silently destroys the user's configuration.
/// </summary>
public sealed class ProjectsService : IProjectsService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public string FilePath { get; }
    private List<ProjectProfile> _items = new();
    public IReadOnlyList<ProjectProfile> All => _items;

    public ProjectsService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CopilotLauncher", "projects.json")) { }

    /// <summary>Test-only ctor.</summary>
    internal ProjectsService(string filePath)
    {
        FilePath = filePath;
        Reload();
    }

    public void Reload()
    {
        if (!File.Exists(FilePath))
        {
            _items = new List<ProjectProfile>();
            return;
        }

        string json;
        try
        {
            json = File.ReadAllText(FilePath);
        }
        catch (IOException)
        {
            // Locked or unreadable: keep the in-memory copy so a later Save
            // can't overwrite good on-disk data with nothing.
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            throw;
        }

        try
        {
            _items = JsonSerializer.Deserialize<List<ProjectProfile>>(json, JsonOpts) ?? new List<ProjectProfile>();
        }
        catch (JsonException)
        {
            try
            {
                var backup = FilePath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
                File.Copy(FilePath, backup, overwrite: true);
            }
            catch
            {
                // Best-effort backup; proceed with the reset either way.
            }
            _items = new List<ProjectProfile>();
        }
    }

    public void Add(ProjectProfile project)
    {
        _items.Add(project);
        SaveAtomic();
    }

    public void Update(ProjectProfile project)
    {
        var idx = _items.FindIndex(x => x.Id == project.Id);
        if (idx < 0) throw new KeyNotFoundException($"No project with id={project.Id}");
        project.UpdatedAt = DateTime.UtcNow;
        _items[idx] = project;
        SaveAtomic();
    }

    public void Remove(string id)
    {
        _items.RemoveAll(x => x.Id == id);
        SaveAtomic();
    }

    public ProjectProfile? GetById(string id) => _items.FirstOrDefault(x => x.Id == id);

    public ProjectProfile? Match(string? directory) => ProjectMatcher.Match(_items, directory);

    public ResolvedLaunchProfile Resolve(string? directory, AppSettings settings) =>
        ProjectMatcher.Resolve(Match(directory), settings);

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
