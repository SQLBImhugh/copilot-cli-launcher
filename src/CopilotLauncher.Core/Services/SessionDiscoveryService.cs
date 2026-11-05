using System.Collections.Concurrent;
using System.Diagnostics;
using CopilotLauncher.Helpers;
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

    /// <summary>
    /// When includeSizes is false, return metadata and any previously calculated
    /// sizes without recursively scanning session artifacts.
    /// </summary>
    IEnumerable<CopilotSession> Enumerate(bool includeSizes, CancellationToken ct = default) => Enumerate();

    /// <summary>Path being scanned. Honors %USERPROFILE% override for testing.</summary>
    string SessionRoot { get; }
}

public sealed class SessionDiscoveryService : ISessionDiscoveryService
{
    private readonly ConcurrentDictionary<string, long> _sizes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, CancellationToken, long> _calculateSize = SafeSumSize;

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

    internal SessionDiscoveryService(string root, Func<string, CancellationToken, long> calculateSize)
        : this(root)
    {
        _calculateSize = calculateSize;
    }

    public IEnumerable<CopilotSession> Enumerate() => Enumerate(includeSizes: true);

    public IEnumerable<CopilotSession> Enumerate(bool includeSizes, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!Directory.Exists(SessionRoot))
            yield break;

        foreach (var dir in Directory.EnumerateDirectories(SessionRoot))
        {
            ct.ThrowIfCancellationRequested();
            CopilotSession? parsed = null;
            try
            {
                parsed = ParseSession(dir, includeSizes, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning("Could not read session {0}: {1}", dir, ex.Message);
            }
            if (parsed is not null)
                yield return parsed;
        }
    }

    private CopilotSession? ParseSession(string folder, bool includeSizes, CancellationToken ct)
    {
        var wsPath = Path.Combine(folder, "workspace.yaml");
        if (!File.Exists(wsPath))
            return null;

        var dict = ReadYamlFlat(wsPath);
        if (dict.Count == 0)
            return null;

        var folderInfo = new DirectoryInfo(folder);
        var size = includeSizes
            ? _calculateSize(folder, ct)
            : _sizes.GetValueOrDefault(folder);
        if (includeSizes)
            _sizes[folder] = size;
        var hasLock = SessionLock.IsInUse(folder);

        return new CopilotSession
        {
            Id           = folderInfo.Name,
            FolderPath   = folder,
            LastModified = folderInfo.LastWriteTimeUtc,
            Name         = NullIfEmpty(dict.GetValueOrDefault("name")),
            Summary      = NullIfEmpty(dict.GetValueOrDefault("summary")),
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

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

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

    private static long SafeSumSize(string folder, CancellationToken ct)
    {
        try
        {
            long size = 0;
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                size += file.Length;
            }
            return size;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Could not calculate session size for {0}: {1}", folder, ex.Message);
            return 0;
        }
    }
}
