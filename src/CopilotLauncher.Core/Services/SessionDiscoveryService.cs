using CopilotLauncher.Models;
using YamlDotNet.RepresentationModel;

namespace CopilotLauncher.Services;

public interface ISessionDiscoveryService
{
    /// <summary>
    /// Enumerate every session under the Copilot CLI session-state directory
    /// (default: <c>%USERPROFILE%\.copilot\session-state</c>). Skips folders that
    /// don't contain a workspace.yaml. Robust against partially-written state.
    /// </summary>
    IEnumerable<CopilotSession> Enumerate();

    /// <summary>Path being scanned. Honors %USERPROFILE% override for testing.</summary>
    string SessionRoot { get; }
}

public sealed class SessionDiscoveryService : ISessionDiscoveryService
{
    public string SessionRoot { get; }

    public SessionDiscoveryService()
    {
        SessionRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".copilot", "session-state");
    }

    /// <summary>Test-only ctor allowing a custom root.</summary>
    internal SessionDiscoveryService(string root)
    {
        SessionRoot = root;
    }

    public IEnumerable<CopilotSession> Enumerate()
    {
        if (!Directory.Exists(SessionRoot))
            yield break;

        foreach (var dir in Directory.EnumerateDirectories(SessionRoot))
        {
            CopilotSession? parsed = null;
            try
            {
                parsed = ParseSession(dir);
            }
            catch
            {
                // Skip unreadable / mid-write sessions silently. Phase 5 will
                // surface these as warnings in a logs panel.
            }
            if (parsed is not null)
                yield return parsed;
        }
    }

    private static CopilotSession? ParseSession(string folder)
    {
        var wsPath = Path.Combine(folder, "workspace.yaml");
        if (!File.Exists(wsPath))
            return null;

        var dict = ReadYamlFlat(wsPath);
        if (dict.Count == 0)
            return null;

        var folderInfo = new DirectoryInfo(folder);
        var size = SafeSumSize(folder);
        var hasLock = folderInfo.EnumerateFiles("inuse.*.lock").Any();

        return new CopilotSession
        {
            Id           = folderInfo.Name,
            FolderPath   = folder,
            LastModified = folderInfo.LastWriteTimeUtc,
            Cwd          = dict.GetValueOrDefault("cwd"),
            Repository   = dict.GetValueOrDefault("repository"),
            Branch       = dict.GetValueOrDefault("branch"),
            GitRoot      = dict.GetValueOrDefault("git_root"),
            HostType     = dict.GetValueOrDefault("host_type"),
            UserNamed    = string.Equals(dict.GetValueOrDefault("user_named"), "true", StringComparison.OrdinalIgnoreCase),
            SummaryCount = int.TryParse(dict.GetValueOrDefault("summary_count"), out var n) ? n : 0,
            CreatedAt    = TryParseDate(dict.GetValueOrDefault("created_at")),
            SizeBytes    = size,
            IsLocked     = hasLock,
        };
    }

    private static Dictionary<string, string> ReadYamlFlat(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var stream = new YamlStream();
            using var reader = new StreamReader(path);
            stream.Load(reader);
            if (stream.Documents.Count == 0)
                return result;
            if (stream.Documents[0].RootNode is YamlMappingNode root)
            {
                foreach (var kv in root.Children)
                {
                    if (kv.Key is YamlScalarNode k && kv.Value is YamlScalarNode v && k.Value is not null)
                        result[k.Value] = v.Value ?? string.Empty;
                }
            }
        }
        catch
        {
            // Tolerate malformed yaml — return whatever we collected.
        }
        return result;
    }

    private static DateTime? TryParseDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return DateTime.TryParse(s, out var dt) ? dt : null;
    }

    private static long SafeSumSize(string folder)
    {
        try
        {
            return new DirectoryInfo(folder)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => f.Length);
        }
        catch
        {
            return 0;
        }
    }
}
