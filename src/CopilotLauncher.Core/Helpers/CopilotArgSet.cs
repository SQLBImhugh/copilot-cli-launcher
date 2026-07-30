using CopilotLauncher.Models;

namespace CopilotLauncher.Helpers;

public sealed class CopilotArgSet
{
    private readonly Dictionary<string, List<string>> _values = new(StringComparer.OrdinalIgnoreCase);

    public string UnrecognizedText { get; set; } = string.Empty;

    public IReadOnlyList<string> GetValues(string flag) =>
        _values.TryGetValue(NormalizeFlag(flag), out var values) ? values : Array.Empty<string>();

    public bool IsEnabled(string flag) => _values.ContainsKey(NormalizeFlag(flag));

    public void SetSwitch(string flag, bool enabled)
    {
        flag = NormalizeFlag(flag);
        if (enabled)
        {
            _values[flag] = new List<string> { string.Empty };
        }
        else
        {
            _values.Remove(flag);
        }
    }

    public void SetValues(string flag, IEnumerable<string> values)
    {
        flag = NormalizeFlag(flag);
        var spec = CopilotArgCatalog.ByFlag[flag];
        if (spec.Kind == CopilotArgValueKind.Switch)
        {
            SetSwitch(flag, true);
            return;
        }

        var clean = values
            .Where(v => v is not null)
            .Select(v => v.Trim())
            .Where(v => v.Length > 0)
            .ToList();
        if (clean.Count == 0)
        {
            _values.Remove(flag);
            return;
        }

        _values[flag] = spec.Repeatable ? clean : new List<string> { clean[0] };
    }

    public void Clear(string flag) => _values.Remove(NormalizeFlag(flag));

    public static CopilotArgSet Parse(string? text)
    {
        var set = new CopilotArgSet();
        var tokens = ArgQuoter.Split(text);
        var unrecognized = new List<string>();

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            var split = SplitAssignment(token);
            var flag = NormalizeFlag(split.Flag);

            if (!CopilotArgCatalog.ByFlag.TryGetValue(flag, out var spec))
            {
                unrecognized.Add(token);
                continue;
            }

            if (spec.Kind == CopilotArgValueKind.Switch)
            {
                AddValue(set._values, flag, string.Empty, spec.Repeatable);
                if (split.Value is { Length: > 0 }) unrecognized.Add(split.Value);
                continue;
            }

            string? value = split.Value;
            if (value is null && i + 1 < tokens.Count && !tokens[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = tokens[++i];
            }

            if (value is null)
            {
                AddValue(set._values, flag, string.Empty, spec.Repeatable);
            }
            else
            {
                AddValue(set._values, flag, value, spec.Repeatable);
            }
        }

        set.UnrecognizedText = ArgQuoter.Format(unrecognized);
        return set;
    }

    public string Format()
    {
        var tokens = new List<string>();
        foreach (var spec in CopilotArgCatalog.All)
        {
            if (!_values.TryGetValue(spec.Flag, out var values)) continue;

            if (spec.Kind == CopilotArgValueKind.Switch)
            {
                var count = Math.Max(1, values.Count);
                for (var i = 0; i < count; i++) tokens.Add(spec.Flag);
                continue;
            }

            foreach (var value in values)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    tokens.Add(spec.Flag);
                }
                else
                {
                    tokens.Add(spec.Flag);
                    tokens.Add(value);
                }

                if (!spec.Repeatable) break;
            }
        }

        var formatted = ArgQuoter.Format(tokens);
        if (string.IsNullOrWhiteSpace(UnrecognizedText)) return formatted;
        if (string.IsNullOrWhiteSpace(formatted)) return UnrecognizedText.Trim();
        return $"{formatted} {UnrecognizedText.Trim()}";
    }

    private static void AddValue(Dictionary<string, List<string>> valuesByFlag, string flag, string value, bool repeatable)
    {
        if (!repeatable || !valuesByFlag.TryGetValue(flag, out var values))
        {
            values = new List<string>();
            valuesByFlag[flag] = values;
        }
        values.Add(value);
    }

    private static (string Flag, string? Value) SplitAssignment(string token)
    {
        var equals = token.IndexOf('=');
        return equals <= 0
            ? (token, null)
            : (token[..equals], token[(equals + 1)..]);
    }

    private static string NormalizeFlag(string flag) =>
        string.Equals(flag, "--reasoning-effort", StringComparison.OrdinalIgnoreCase) ? "--effort" : flag;
}

