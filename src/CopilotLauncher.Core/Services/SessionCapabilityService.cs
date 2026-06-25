using System.Diagnostics;
using System.Text.Json;
using CopilotLauncher.Helpers;
using CopilotLauncher.Models;

namespace CopilotLauncher.Services;

/// <summary>
/// Enumerates the capabilities the copilot CLI can load for a given working
/// directory: MCP servers and skills (via <c>copilot mcp list --json</c> /
/// <c>copilot skill list --json</c>, which include workspace + plugin sources)
/// and custom agents (scanned from the usual agent folders). Used to populate
/// the capability selector UI. Best-effort: never throws; missing/failed
/// sources just come back empty.
/// </summary>
public interface ISessionCapabilityService
{
    Task<CapabilityCatalog> DiscoverAsync(string? workingDirectory, bool forceRefresh = false, CancellationToken ct = default);
}

public sealed class SessionCapabilityService : ISessionCapabilityService
{
    /// <summary>Runs the copilot CLI with the given args in <paramref name="workingDir"/> and
    /// returns stdout (or null on failure). Injected so tests can supply canned JSON.</summary>
    internal delegate Task<string?> CliRunner(IReadOnlyList<string> args, string? workingDir, CancellationToken ct);

    private readonly CliRunner _runCli;

    private readonly object _lock = new();
    private string? _cachedKey;
    private CapabilityCatalog? _cached;

    public SessionCapabilityService() : this(RunCopilotAsync) { }

    /// <summary>Test-only ctor.</summary>
    internal SessionCapabilityService(CliRunner runCli) => _runCli = runCli;

    public async Task<CapabilityCatalog> DiscoverAsync(string? workingDirectory, bool forceRefresh = false, CancellationToken ct = default)
    {
        var key = workingDirectory ?? string.Empty;
        if (!forceRefresh)
        {
            lock (_lock)
            {
                if (_cached is not null && string.Equals(_cachedKey, key, StringComparison.OrdinalIgnoreCase))
                    return _cached;
            }
        }

        var mcpJson = await SafeAsync(() => _runCli(new[] { "mcp", "list", "--json" }, workingDirectory, ct)).ConfigureAwait(false);
        var skillJson = await SafeAsync(() => _runCli(new[] { "skill", "list", "--json" }, workingDirectory, ct)).ConfigureAwait(false);

        var catalog = new CapabilityCatalog
        {
            McpServers = ParseMcpServers(mcpJson),
            Skills = ParseSkills(skillJson),
            Agents = ScanAgents(workingDirectory),
        };

        lock (_lock) { _cached = catalog; _cachedKey = key; }
        return catalog;
    }

    // ----- parsing (internal for tests) -----

    internal static IReadOnlyList<McpServerInfo> ParseMcpServers(string? json)
    {
        var list = new List<McpServerInfo>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("mcpServers", out var servers) || servers.ValueKind != JsonValueKind.Object)
                return list;
            foreach (var prop in servers.EnumerateObject())
            {
                // Only read name/source/type — never the headers/url (they can hold auth tokens).
                var v = prop.Value;
                var source = v.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()! : string.Empty;
                var type = v.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : string.Empty;
                list.Add(new McpServerInfo { Name = prop.Name, Source = source, Type = type });
            }
        }
        catch (JsonException) { return new List<McpServerInfo>(); }
        return list.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal static IReadOnlyList<SkillInfo> ParseSkills(string? json)
    {
        var list = new List<SkillInfo>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var name = el.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : string.Empty;
                if (string.IsNullOrWhiteSpace(name)) continue;
                var source = el.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()! : string.Empty;
                var desc = el.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString()! : string.Empty;
                list.Add(new SkillInfo { Name = name, Source = source, Description = desc });
            }
        }
        catch (JsonException) { return new List<SkillInfo>(); }
        return list.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Best-effort scan for custom agent definitions (one *.md per agent) in the
    /// usual project + personal locations. The UI also allows free-text entry, so misses are fine.</summary>
    internal static IReadOnlyList<string> ScanAgents(string? workingDirectory)
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var dirs = new List<string>();
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            foreach (var rel in new[] { @".github\agents", @".copilot\agents", @".agents\agents", @".claude\agents" })
                dirs.Add(Path.Combine(workingDirectory, rel));
        }
        dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot", "agents"));

        foreach (var dir in dirs)
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.EnumerateFiles(dir, "*.md", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileNameWithoutExtension(f);
                    if (!string.IsNullOrWhiteSpace(name) && !name.Equals("README", StringComparison.OrdinalIgnoreCase))
                        names.Add(name);
                }
            }
            catch { /* unreadable dir — skip */ }
        }
        return names.ToList();
    }

    // ----- real CLI runner -----

    private static async Task<string?> RunCopilotAsync(IReadOnlyList<string> args, string? workingDir, CancellationToken ct)
    {
        var target = ProcessUtil.Resolve(TerminalDiscoveryService.ResolveOnPath);
        if (target is null) return null;

        var psi = new ProcessStartInfo
        {
            FileName = target.FileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (!string.IsNullOrWhiteSpace(workingDir) && Directory.Exists(workingDir))
            psi.WorkingDirectory = workingDir;
        foreach (var a in target.PrefixArgs) psi.ArgumentList.Add(a);
        foreach (var a in args) psi.ArgumentList.Add(a);

        Process? p = null;
        try
        {
            p = Process.Start(psi);
            if (p is null) return null;
            var readTask = p.StandardOutput.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            try
            {
                await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            return await readTask.ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
        finally
        {
            p?.Dispose();
        }
    }

    private static async Task<string?> SafeAsync(Func<Task<string?>> f)
    {
        try { return await f().ConfigureAwait(false); }
        catch { return null; }
    }
}
