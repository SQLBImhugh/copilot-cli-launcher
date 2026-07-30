using System.Collections.ObjectModel;

namespace CopilotLauncher.Models;

public enum CopilotArgValueKind
{
    Switch,
    Choice,
    Text,
    Number,
    Path,
}

public sealed class CopilotArgSpec
{
    public required string Flag { get; init; }
    public required string Description { get; init; }
    public required CopilotArgValueKind Kind { get; init; }
    public IReadOnlyList<string> Choices { get; init; } = Array.Empty<string>();
    public string Placeholder { get; init; } = string.Empty;
    public bool Repeatable { get; init; }
    public required string Category { get; init; }
}

public static class CopilotArgCatalog
{
    private static readonly IReadOnlyDictionary<string, CopilotArgSpec> ByFlagValue;

    public static IReadOnlyList<CopilotArgSpec> All { get; }

    public static IReadOnlyDictionary<string, CopilotArgSpec> ByFlag => ByFlagValue;

    static CopilotArgCatalog()
    {
        All = new ReadOnlyCollection<CopilotArgSpec>(new[]
        {
            Text("--model", "Set the AI model to use (use 'auto' to let Copilot pick automatically)", "Model & reasoning", "auto", false, "auto", "gpt-5.4", "gpt-5.5", "gpt-5.3-codex", "claude-sonnet-4.5", "claude-opus-4.8", "gemini-3.1-pro-preview"),
            Choice("--effort", "Set the reasoning effort level", "Model & reasoning", "none", "minimal", "low", "medium", "high", "xhigh", "max"),
            Choice("--context", "Set the context tier", "Model & reasoning", "default", "long_context"),
            Choice("--stream", "Set streaming mode", "Model & reasoning", "on", "off"),

            Switch("--autopilot", "Start in autopilot mode", "Mode"),
            Switch("--plan", "Start in plan mode", "Mode"),
            Choice("--mode", "Set the startup mode", "Mode", "interactive", "plan", "autopilot"),
            Number("--max-autopilot-continues", "Set the maximum autopilot continues", "Mode", "5"),

            Switch("--allow-all-tools", "Allow all tools", "Permissions"),
            Switch("--allow-all-paths", "Allow all paths", "Permissions"),
            Switch("--allow-all-urls", "Allow all URLs", "Permissions"),
            Text("--allow-tool", "Allow a tool pattern", "Permissions", "shell(git:*)", true),
            Text("--deny-tool", "Deny a tool pattern", "Permissions", "shell(rm:*)", true),
            Text("--allow-url", "Allow a URL pattern", "Permissions", "https://example.com/*", true),
            Text("--deny-url", "Deny a URL pattern", "Permissions", "https://example.com/*", true),
            Path("--add-dir", "Allow an additional directory", "Permissions", "C:\\path\\to\\directory", true),
            Switch("--disallow-temp-dir", "Disallow temporary directory access", "Permissions"),

            Text("--name", "Set the session name", "Session", "My session"),
            Switch("--continue", "Resume the most recent session", "Session"),
            Text("--session-id", "Resume a specific session ID", "Session", "session-id"),
            Switch("--enable-memory", "Enable memory", "Session"),
            Switch("--no-ask-user", "Disable ask-user prompts", "Session"),

            Switch("--enable-all-github-mcp-tools", "Enable all GitHub MCP tools", "MCP & GitHub tools"),
            Text("--add-github-mcp-toolset", "Add a GitHub MCP toolset", "MCP & GitHub tools", "toolset", true),
            Text("--add-github-mcp-tool", "Add a GitHub MCP tool", "MCP & GitHub tools", "tool", true),
            Switch("--allow-all-mcp-server-instructions", "Allow all MCP server instructions", "MCP & GitHub tools"),

            Switch("--no-custom-instructions", "Disable custom instructions", "Instructions"),

            Switch("--banner", "Show the banner", "Output & UI"),
            Switch("--no-color", "Disable color output", "Output & UI"),
            Switch("--screen-reader", "Enable screen-reader mode", "Output & UI"),
            Switch("--plain-diff", "Use plain diff output", "Output & UI"),
            Choice("--mouse", "Set mouse mode", "Output & UI", "on", "off"),
            Choice("--output-format", "Set output format", "Output & UI", "text", "json"),

            Switch("--remote", "Enable remote mode", "Remote"),
            Switch("--no-remote", "Disable remote mode", "Remote"),
            Switch("--remote-export", "Enable remote export", "Remote"),
            Switch("--no-remote-export", "Disable remote export", "Remote"),

            Choice("--log-level", "Set log level", "Logging & limits", "none", "error", "warning", "info", "debug", "all", "default"),
            Path("--log-dir", "Set log directory", "Logging & limits", "C:\\path\\to\\logs"),
            Number("--max-ai-credits", "Set maximum AI credits", "Logging & limits", "100"),

            Switch("--experimental", "Enable experimental features", "Advanced"),
            Switch("--no-experimental", "Disable experimental features", "Advanced"),
            Switch("--no-auto-update", "Disable auto-update", "Advanced"),
            Choice("--bash-env", "Set bash environment handling", "Advanced", "on", "off"),
            Text("--secret-env-vars", "Set secret environment variables", "Advanced", "VAR1,VAR2"),
            Path("--plugin-dir", "Add a plugin directory", "Advanced", "C:\\path\\to\\plugins", true),
        });

        // Flags with dedicated UI elsewhere are deliberately excluded to avoid duplicate or conflicting output.
        ByFlagValue = All.ToDictionary(s => s.Flag, StringComparer.OrdinalIgnoreCase);
    }

    public static bool TryGet(string flag, out CopilotArgSpec spec) => ByFlagValue.TryGetValue(flag, out spec!);

    private static CopilotArgSpec Switch(string flag, string description, string category) => new()
    {
        Flag = flag,
        Description = description,
        Kind = CopilotArgValueKind.Switch,
        Category = category,
    };

    private static CopilotArgSpec Choice(string flag, string description, string category, params string[] choices) => new()
    {
        Flag = flag,
        Description = description,
        Kind = CopilotArgValueKind.Choice,
        Choices = choices,
        Placeholder = choices.FirstOrDefault() ?? string.Empty,
        Category = category,
    };

    private static CopilotArgSpec Text(string flag, string description, string category, string placeholder, bool repeatable = false, params string[] suggestions) => new()
    {
        Flag = flag,
        Description = description,
        Kind = CopilotArgValueKind.Text,
        Choices = suggestions,
        Placeholder = placeholder,
        Repeatable = repeatable,
        Category = category,
    };

    private static CopilotArgSpec Number(string flag, string description, string category, string placeholder) => new()
    {
        Flag = flag,
        Description = description,
        Kind = CopilotArgValueKind.Number,
        Placeholder = placeholder,
        Category = category,
    };

    private static CopilotArgSpec Path(string flag, string description, string category, string placeholder, bool repeatable = false) => new()
    {
        Flag = flag,
        Description = description,
        Kind = CopilotArgValueKind.Path,
        Placeholder = placeholder,
        Repeatable = repeatable,
        Category = category,
    };
}
