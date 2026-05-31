using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace CopilotLauncher.Services;

public interface IReleaseNotesService
{
    /// <summary>
    /// Fetch GitHub release notes for the github/copilot-cli repo and return
    /// the entries in the half-open range <c>(fromVersion, toVersion]</c>
    /// (excluding fromVersion, including toVersion), sorted oldest-first so
    /// the briefing body reads in chronological order. Returns an empty list
    /// if the API is unreachable, the response is unparseable, or no
    /// releases match — never throws.
    /// </summary>
    Task<IReadOnlyList<ReleaseEntry>> FetchAsync(
        string fromVersion,
        string toVersion,
        CancellationToken ct = default);
}

public sealed class ReleaseNotesService : IReleaseNotesService
{
    private const string ApiUrl = "https://api.github.com/repos/github/copilot-cli/releases?per_page=50";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    // Static so all instances share a single connection pool (HttpClient is
    // intended to be long-lived). UA header is set per request to keep the
    // client immutable for testability.
    private static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly ISettingsService _settings;
    private readonly Func<CancellationToken, Task<string?>> _fetchJson;

    public ReleaseNotesService(ISettingsService settings)
        : this(settings, DefaultFetchAsync) { }

    /// <summary>Test-only ctor.</summary>
    internal ReleaseNotesService(
        ISettingsService settings,
        Func<CancellationToken, Task<string?>> fetchJson)
    {
        _settings = settings;
        _fetchJson = fetchJson;
    }

    public async Task<IReadOnlyList<ReleaseEntry>> FetchAsync(
        string fromVersion,
        string toVersion,
        CancellationToken ct = default)
    {
        try
        {
            var json = await GetCachedOrFreshJsonAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json)) return Array.Empty<ReleaseEntry>();

            var all = ParseReleases(json);
            return FilterRange(all, fromVersion, toVersion);
        }
        catch
        {
            // Best-effort. Network outage / parse failure / disk error all
            // collapse to "no release notes available", which the caller
            // handles by falling back to the raw `copilot update` output.
            return Array.Empty<ReleaseEntry>();
        }
    }

    /// <summary>
    /// Filter parsed releases to the half-open range (fromVersion, toVersion]
    /// and sort oldest-first. Public + static so tests can hit it directly.
    /// </summary>
    internal static IReadOnlyList<ReleaseEntry> FilterRange(
        IReadOnlyList<ReleaseEntry> all,
        string fromVersion,
        string toVersion)
    {
        var from = TryParseSemVer(fromVersion);
        var to = TryParseSemVer(toVersion);
        var filtered = new List<(ReleaseEntry Entry, Version Parsed)>();
        foreach (var e in all)
        {
            var v = TryParseSemVer(e.Version);
            if (v is null) continue;
            // (from, to]
            if (from is not null && v.CompareTo(from) <= 0) continue;
            if (to is not null && v.CompareTo(to) > 0) continue;
            filtered.Add((e, v));
        }
        return filtered
            .OrderBy(p => p.Parsed)
            .Select(p => p.Entry)
            .ToList();
    }

    /// <summary>Public + static for tests.</summary>
    internal static IReadOnlyList<ReleaseEntry> ParseReleases(string json)
    {
        var list = new List<ReleaseEntry>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            string? tag = null;
            string? body = null;
            DateTime? date = null;
            if (el.TryGetProperty("tag_name", out var tagEl) && tagEl.ValueKind == JsonValueKind.String)
                tag = tagEl.GetString();
            if (el.TryGetProperty("body", out var bodyEl) && bodyEl.ValueKind == JsonValueKind.String)
                body = bodyEl.GetString();
            if (el.TryGetProperty("published_at", out var dateEl) && dateEl.ValueKind == JsonValueKind.String
                && DateTime.TryParse(dateEl.GetString(), out var parsedDate))
                date = parsedDate;
            if (string.IsNullOrWhiteSpace(tag)) continue;
            // Strip leading "v" so version compares cleanly.
            var version = tag.TrimStart('v', 'V');
            // Skip pre-release tags (e.g. "1.0.56-2", "1.0.57-0"). copilot CLI
            // publishes daily pre-releases between stable cuts, and including
            // them would (a) duplicate the stable version's row when the
            // parser strips the suffix and (b) drown the briefing in
            // unreleased text. Stable users only care about stable -> stable.
            if (version.Contains('-')) continue;
            list.Add(new ReleaseEntry
            {
                Version = version,
                Date = date,
                Body = body,
            });
        }
        return list;
    }

    /// <summary>
    /// Build a flat changelog text suitable for the AI prompt. Each entry
    /// becomes a "## vX.Y.Z\n\n&lt;body&gt;" block. Returns empty string when
    /// entries is empty so callers can fall back to raw `copilot update`
    /// output. Public so the WinUI startup-update path can call it.
    /// </summary>
    public static string BuildChangelogText(IEnumerable<ReleaseEntry> entries)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var e in entries)
        {
            sb.Append("## v").AppendLine(e.Version);
            if (e.Date is DateTime d)
                sb.AppendLine(d.ToString("yyyy-MM-dd"));
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(e.Body))
                sb.AppendLine(e.Body.TrimEnd());
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    private async Task<string?> GetCachedOrFreshJsonAsync(CancellationToken ct)
    {
        var cacheFile = Path.Combine(_settings.AppDataDirectory, "state", "releases-cache.json");
        try
        {
            if (File.Exists(cacheFile))
            {
                var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(cacheFile);
                if (age < CacheTtl)
                {
                    return await File.ReadAllTextAsync(cacheFile, ct).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            // Treat unreadable cache as a miss.
        }

        var fresh = await _fetchJson(ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(fresh))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
                await File.WriteAllTextAsync(cacheFile, fresh, ct).ConfigureAwait(false);
            }
            catch
            {
                // Caching is best-effort.
            }
        }
        return fresh;
    }

    private static async Task<string?> DefaultFetchAsync(CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, ApiUrl);
            // GitHub requires a User-Agent header on all API requests; anonymous
            // calls otherwise return 403. The launcher version isn't strictly
            // needed but helps with debugging if GitHub ever asks.
            req.Headers.UserAgent.ParseAdd("CopilotLauncher/0.1.11 (+https://github.com/SQLBImhugh/copilot-cli-launcher)");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var resp = await SharedClient.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private static Version? TryParseSemVer(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        // Accept "1.0.56", "v1.0.56", "1.0.56-beta1". The pre-release suffix
        // is stripped so semver-without-pre-release ordering applies. Good
        // enough for copilot-cli which doesn't ship pre-release tags via the
        // Releases API.
        var m = Regex.Match(raw.Trim().TrimStart('v', 'V'), @"^(\d+)\.(\d+)\.(\d+)");
        if (!m.Success) return null;
        return new Version(
            int.Parse(m.Groups[1].Value),
            int.Parse(m.Groups[2].Value),
            int.Parse(m.Groups[3].Value));
    }
}
