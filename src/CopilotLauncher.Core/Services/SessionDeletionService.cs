using CopilotLauncher.Models;

namespace CopilotLauncher.Services;

/// <summary>Outcome of deleting one session.</summary>
public sealed class SessionDeleteResult
{
    public required string SessionId { get; init; }
    public bool Deleted { get; init; }

    /// <summary>Why it was refused or failed. Null when <see cref="Deleted"/> is true.</summary>
    public string? Error { get; init; }

    /// <summary>Bytes reclaimed (0 when not deleted).</summary>
    public long BytesFreed { get; init; }
}

/// <summary>Aggregate outcome of a bulk delete.</summary>
public sealed class BulkDeleteResult
{
    public required IReadOnlyList<SessionDeleteResult> Results { get; init; }

    public int DeletedCount => Results.Count(r => r.Deleted);
    public int FailedCount => Results.Count(r => !r.Deleted);
    public long BytesFreed => Results.Sum(r => r.BytesFreed);

    public IEnumerable<SessionDeleteResult> Failures => Results.Where(r => !r.Deleted);
}

/// <summary>
/// Permanently deletes Copilot session folders under
/// <c>~/.copilot/session-state/</c>.
/// </summary>
/// <remarks>
/// Deliberately conservative, because this is unrecoverable:
/// <list type="bullet">
/// <item>Refuses any session that is currently in use (a sibling <c>inuse.*.lock</c> exists) —
/// deleting a live session's state corrupts the running CLI.</item>
/// <item>Refuses any path that does not resolve to a direct child of the session root, so a
/// malformed or hostile id can't escape the store.</item>
/// <item>Appends every deletion to <c>~/.copilot/deleted-sessions.log</c> so there is a record
/// of what was removed even though the data itself is gone.</item>
/// </list>
/// </remarks>
public interface ISessionDeletionService
{
    /// <summary>Delete one session by id. Never throws.</summary>
    SessionDeleteResult Delete(string sessionId);

    /// <summary>Delete several sessions. Continues past individual failures.</summary>
    BulkDeleteResult DeleteMany(IEnumerable<string> sessionIds);
}

public sealed class SessionDeletionService : ISessionDeletionService
{
    private readonly string _sessionRoot;
    private readonly string _logPath;

    public SessionDeletionService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".copilot", "session-state")) { }

    /// <summary>Test-only ctor.</summary>
    internal SessionDeletionService(string sessionRoot)
    {
        _sessionRoot = sessionRoot;
        var parent = Path.GetDirectoryName(sessionRoot);
        _logPath = Path.Combine(
            string.IsNullOrEmpty(parent) ? sessionRoot : parent,
            "deleted-sessions.log");
    }

    public SessionDeleteResult Delete(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return Fail(sessionId ?? string.Empty, "No session id supplied.");

        string dir;
        try
        {
            dir = Path.GetFullPath(Path.Combine(_sessionRoot, sessionId.Trim()));
        }
        catch (Exception ex)
        {
            return Fail(sessionId, $"Invalid session id: {ex.Message}");
        }

        // The resolved path must be a DIRECT child of the session root. Blocks "..",
        // absolute ids, and anything else that would reach outside the store.
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_sessionRoot));
        var parent = Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(dir) ?? string.Empty);
        if (!string.Equals(parent, root, StringComparison.OrdinalIgnoreCase))
            return Fail(sessionId, "Refused: path is outside the session store.");

        if (!Directory.Exists(dir))
            return Fail(sessionId, "Session folder no longer exists.");

        try
        {
            if (Directory.EnumerateFiles(dir, "inuse.*.lock").Any())
                return Fail(sessionId, "Session is currently in use — close it first.");
        }
        catch (Exception ex)
        {
            return Fail(sessionId, $"Could not check lock state: {ex.Message}");
        }

        var bytes = SafeSize(dir);

        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            return Fail(sessionId, ex.Message);
        }

        AppendLog(sessionId, bytes);

        return new SessionDeleteResult { SessionId = sessionId, Deleted = true, BytesFreed = bytes };
    }

    public BulkDeleteResult DeleteMany(IEnumerable<string> sessionIds)
    {
        var results = new List<SessionDeleteResult>();
        foreach (var id in sessionIds ?? Enumerable.Empty<string>())
            results.Add(Delete(id));
        return new BulkDeleteResult { Results = results };
    }

    private static SessionDeleteResult Fail(string id, string error) =>
        new() { SessionId = id, Deleted = false, Error = error };

    private static long SafeSize(string dir)
    {
        try
        {
            return new DirectoryInfo(dir)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => f.Length);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Best-effort audit trail. A logging failure must never block the delete.</summary>
    private void AppendLog(string sessionId, long bytes)
    {
        try
        {
            var line = $"{DateTime.UtcNow:o}\t{sessionId}\t{bytes}";
            File.AppendAllLines(_logPath, new[] { line });
        }
        catch
        {
            // Ignored — the session is already gone; losing the log line is not worth surfacing.
        }
    }
}
