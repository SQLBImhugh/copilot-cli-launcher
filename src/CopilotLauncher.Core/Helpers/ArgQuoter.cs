using System.Text;

namespace CopilotLauncher.Helpers;

/// <summary>
/// Builds the Arguments string for a Windows .lnk so each individual argument
/// survives Windows command-line re-parsing. Wraps any value containing
/// whitespace or double-quotes in double-quotes; embedded quotes are escaped
/// by doubling. Anything safe is passed through unwrapped.
///
/// Direct port of Format-ShortcutArgs from the legacy PS launcher.
/// See <see cref="Tests.ArgQuoterTests"/> for the fixture set.
/// </summary>
public static class ArgQuoter
{
    public static string Format(IEnumerable<string?> arguments)
    {
        var sb = new StringBuilder();
        var first = true;
        foreach (var raw in arguments)
        {
            if (string.IsNullOrEmpty(raw)) continue;
            if (!first) sb.Append(' ');
            first = false;

            if (NeedsQuoting(raw))
            {
                sb.Append('"').Append(raw.Replace("\"", "\"\"")).Append('"');
            }
            else
            {
                sb.Append(raw);
            }
        }
        return sb.ToString();
    }

    private static bool NeedsQuoting(string s)
    {
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c) || c == '"') return true;
        }
        return false;
    }

    /// <summary>
    /// Tokenize a Windows-style command-line fragment, preserving double-quoted
    /// spans as a single token. Used for parsing user-entered ExtraCopilotArgs
    /// so values like '--prompt "do the thing"' survive as a single argument.
    /// </summary>
    public static IReadOnlyList<string> Split(string? line)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(line)) return result;

        var sb = new StringBuilder();
        var inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }
            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (sb.Length > 0)
                {
                    result.Add(sb.ToString());
                    sb.Clear();
                }
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0)
            result.Add(sb.ToString());
        return result;
    }
}
