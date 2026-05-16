namespace CopilotLauncher.Helpers;

public static class PathValidator
{
    /// <summary>Returns the normalized full path if valid and existing on disk; otherwise null.</summary>
    public static string? ValidateWorkingDirectory(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        try
        {
            var trimmed = raw.Trim();
            if (!Path.IsPathFullyQualified(trimmed))
                return null;

            var full = Path.GetFullPath(trimmed);
            return Directory.Exists(full) ? full : null;
        }
        catch
        {
            return null;
        }
    }
}
