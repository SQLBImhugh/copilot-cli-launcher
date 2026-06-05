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
        var filtered = new List<(ReleaseEntry Entry, SemVerKey Parsed)>();
        foreach (var e in all)
        {
            var v = TryParseSemVer(e.Version);
            if (v is null) continue;
            // (from, to]
            if (from is not null && v.Value.CompareTo(from.Value) <= 0) continue;
            if (to is not null && v.Value.CompareTo(to.Value) > 0) continue;
            filtered.Add((e, v.Value));
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
            // Keep the full tag (incl. any "-N" pre-release suffix) so the
            // briefing displays the real tag name and the AI prompt gets
            // accurate version labels. copilot CLI ships daily pre-release
            // builds (v1.0.57-0, -1, -2, ...) between stable cuts; when a
            // user is on a pre-release that has no matching stable yet,
            // skipping them produced an empty changelog and forced the AI
            // to hallucinate from session memory. v0.1.12 includes them.
            var version = tag.TrimStart('v', 'V');
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
        string? cachedContent = null;
        try
        {
            if (File.Exists(cacheFile))
            {
                var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(cacheFile);
                cachedContent = await File.ReadAllTextAsync(cacheFile, ct).ConfigureAwait(false);
                if (age < CacheTtl)
                    return cachedContent;
            }
        }
        catch
        {
            // Treat unreadable cache as a miss.
            cachedContent = null;
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
            return fresh;
        }
        // Fresh fetch failed (network outage, rate limit, transient API error).
        // Fall back to stale cache rather than returning empty — stale release
        // notes are infinitely better than zero notes, because the AI summary
        // will otherwise have nothing to anchor on and will hallucinate from
        // session memory. The cache JSON itself is the only ground truth the
        // launcher has about historical releases.
        return cachedContent;
    }

    private static async Task<string?> DefaultFetchAsync(CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, ApiUrl);
            // GitHub requires a User-Agent header on all API requests; anonymous
            // calls otherwise return 403. The launcher version isn't strictly
            // needed but helps with debugging if GitHub ever asks.
            req.Headers.UserAgent.ParseAdd("CopilotLauncher/0.2.0 (+https://github.com/SQLBImhugh/copilot-cli-launcher)");
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

    /// <summary>
    /// Parse "1.0.56", "v1.0.56", "1.0.57-3" into a comparable key that orders
    /// pre-release tags BEFORE the stable cut of the same base version
    /// (1.0.56-0 &lt; 1.0.56-1 &lt; 1.0.56-2 &lt; 1.0.56). copilot CLI ships
    /// numeric pre-release suffixes (-N) so this only handles integer suffixes;
    /// non-numeric suffixes (-beta1) are treated as pre-release zero. Public+
    /// static so tests can exercise ordering directly.
    /// </summary>
    internal static SemVerKey? TryParseSemVer(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim().TrimStart('v', 'V');
        var m = Regex.Match(trimmed, @"^(\d+)\.(\d+)\.(\d+)(?:-([\w\.]+))?");
        if (!m.Success) return null;
        // Stable releases get int.MaxValue so they sort after any pre-release
        // of the same base version. Numeric pre-release suffixes parse
        // directly. Non-numeric suffixes fall back to 0 (rare for copilot CLI).
        int preReleaseRank;
        if (!m.Groups[4].Success)
        {
            preReleaseRank = int.MaxValue;
        }
        else if (!int.TryParse(m.Groups[4].Value, out preReleaseRank))
        {
            preReleaseRank = 0;
        }
        return new SemVerKey(
            int.Parse(m.Groups[1].Value),
            int.Parse(m.Groups[2].Value),
            int.Parse(m.Groups[3].Value),
            preReleaseRank);
    }
}

/// <summary>
/// Semver comparable key with explicit pre-release ordering. Stable releases
/// use <see cref="int.MaxValue"/> for <see cref="PreReleaseRank"/> so they
/// always sort after every pre-release of the same major.minor.patch.
/// Internal + nested in the same file as ReleaseNotesService since it's only
/// used by FilterRange + tests.
/// </summary>
internal readonly record struct SemVerKey(int Major, int Minor, int Patch, int PreReleaseRank)
    : IComparable<SemVerKey>
{
    public int CompareTo(SemVerKey other)
    {
        var c = Major.CompareTo(other.Major); if (c != 0) return c;
        c = Minor.CompareTo(other.Minor); if (c != 0) return c;
        c = Patch.CompareTo(other.Patch); if (c != 0) return c;
        return PreReleaseRank.CompareTo(other.PreReleaseRank);
    }
}
