using System.Text.Json;
using System.Text.Json.Nodes;

namespace CopilotLauncher.Services;

/// <summary>
/// Pre-approves copilot CLI extension "elevated permissions" for a directory so
/// copilot does not re-prompt "Extension X wants elevated permissions /
/// skip tool permission prompts" every time a session is resumed/started there.
/// </summary>
/// <remarks>
/// copilot gates extension elevated permissions per-directory in
/// <c>~/.copilot/permissions-config.json</c> under
/// <c>locations[&lt;dir&gt;].tool_approvals[]</c> as entries of shape
/// <c>{ "kind": "extension-permission-access", "extensionName": "user:&lt;name&gt;" }</c>.
/// These are written when the user picks "Yes, and always allow &lt;ext&gt; in this
/// repo". A copilot update can reset that store, re-triggering the prompts —
/// and <c>--allow-all</c> (tools/paths/urls) does NOT cover them. This service
/// re-asserts grants for every installed user extension (the subdirectories of
/// <c>~/.copilot/extensions</c>) at a given directory, merging non-destructively
/// so existing grants and other approval kinds (commands/write/project:*) are
/// preserved.
/// </remarks>
public interface IExtensionPermissionService
{
    /// <summary>
    /// Ensure permissions-config.json grants <c>extension-permission-access</c>
    /// for every installed user extension at <paramref name="directory"/>.
    /// Returns the number of NEW grants written (0 if none were needed, the
    /// directory is blank, there are no installed extensions, or on any I/O /
    /// parse error). Best-effort — never throws.
    /// </summary>
    int EnsureExtensionGrants(string directory);
}

public sealed class ExtensionPermissionService : IExtensionPermissionService
{
    private const string ExtensionAccessKind = "extension-permission-access";

    private readonly string _extensionsRoot;
    private readonly string _permissionsConfigPath;

    public ExtensionPermissionService()
    {
        var copilotHome = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".copilot");
        _extensionsRoot = Path.Combine(copilotHome, "extensions");
        _permissionsConfigPath = Path.Combine(copilotHome, "permissions-config.json");
    }

    /// <summary>Test-only ctor.</summary>
    internal ExtensionPermissionService(string extensionsRoot, string permissionsConfigPath)
    {
        _extensionsRoot = extensionsRoot;
        _permissionsConfigPath = permissionsConfigPath;
    }

    public int EnsureExtensionGrants(string directory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(directory)) return 0;

            // Installed user extensions = subdirectories of ~/.copilot/extensions.
            // Each maps to the "user:<dirname>" identifier copilot uses.
            var desired = EnumerateUserExtensionIdentifiers();
            if (desired.Count == 0) return 0;

            var normalizedDir = NormalizeDirectory(directory);

            // Load existing config non-destructively. A missing file is fine
            // (we create it). A present-but-unparseable file is left ALONE — we
            // never clobber copilot's config we couldn't read.
            JsonObject root;
            if (File.Exists(_permissionsConfigPath))
            {
                JsonNode? parsed;
                try { parsed = JsonNode.Parse(File.ReadAllText(_permissionsConfigPath)); }
                catch (JsonException) { return 0; }
                if (parsed is not JsonObject obj) return 0;
                root = obj;
            }
            else
            {
                root = new JsonObject();
            }

            if (root["locations"] is not JsonObject locations)
            {
                locations = new JsonObject();
                root["locations"] = locations;
            }

            // Reuse an existing location key that resolves to the same path
            // (Windows paths are case-insensitive), else add a new one.
            var locationKey = FindMatchingLocationKey(locations, normalizedDir) ?? normalizedDir;
            if (locations[locationKey] is not JsonObject location)
            {
                location = new JsonObject();
                locations[locationKey] = location;
            }

            if (location["tool_approvals"] is not JsonArray approvals)
            {
                approvals = new JsonArray();
                location["tool_approvals"] = approvals;
            }

            var existing = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in approvals)
            {
                if (node is JsonObject o
                    && o["kind"]?.GetValue<string>() == ExtensionAccessKind
                    && o["extensionName"]?.GetValue<string>() is string name
                    && !string.IsNullOrEmpty(name))
                {
                    existing.Add(name);
                }
            }

            var added = 0;
            foreach (var id in desired)
            {
                if (existing.Contains(id)) continue;
                approvals.Add(new JsonObject
                {
                    ["kind"] = ExtensionAccessKind,
                    ["extensionName"] = id,
                });
                existing.Add(id);
                added++;
            }

            if (added == 0) return 0;

            WriteAtomic(root);
            return added;
        }
        catch
        {
            // Best-effort: a failure here just means copilot will prompt as usual.
            return 0;
        }
    }

    private List<string> EnumerateUserExtensionIdentifiers()
    {
        var result = new List<string>();
        if (!Directory.Exists(_extensionsRoot)) return result;
        foreach (var dir in Directory.EnumerateDirectories(_extensionsRoot))
        {
            var name = new DirectoryInfo(dir).Name;
            if (!string.IsNullOrWhiteSpace(name))
                result.Add("user:" + name);
        }
        return result;
    }

    private static string NormalizeDirectory(string directory)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        }
        catch
        {
            return directory.Trim();
        }
    }

    private static string? FindMatchingLocationKey(JsonObject locations, string normalizedDir)
    {
        foreach (var kvp in locations)
        {
            if (string.Equals(NormalizeDirectory(kvp.Key), normalizedDir, StringComparison.OrdinalIgnoreCase))
                return kvp.Key;
        }
        return null;
    }

    private void WriteAtomic(JsonObject root)
    {
        var dir = Path.GetDirectoryName(_permissionsConfigPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Back up the existing file once before the first mutation so a bad
        // write (or an unexpected copilot schema) is recoverable.
        if (File.Exists(_permissionsConfigPath))
        {
            var backup = _permissionsConfigPath + ".bak-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            try { if (!File.Exists(backup)) File.Copy(_permissionsConfigPath, backup); }
            catch { /* best effort */ }
        }

        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var tmp = _permissionsConfigPath + ".tmp";
        File.WriteAllText(tmp, json);
        if (File.Exists(_permissionsConfigPath))
            File.Replace(tmp, _permissionsConfigPath, destinationBackupFileName: null);
        else
            File.Move(tmp, _permissionsConfigPath);
    }
}
