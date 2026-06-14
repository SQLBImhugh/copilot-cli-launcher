using System.Text.Json.Nodes;
using CopilotLauncher.Services;
using Xunit;

namespace CopilotLauncher.Tests;

public class ExtensionPermissionServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _extRoot;
    private readonly string _permPath;

    public ExtensionPermissionServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "copilot-launcher-extperm-" + Guid.NewGuid());
        _extRoot = Path.Combine(_root, "extensions");
        Directory.CreateDirectory(_extRoot);
        _permPath = Path.Combine(_root, "permissions-config.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best effort */ }
    }

    private ExtensionPermissionService NewSvc() => new(_extRoot, _permPath);

    private void MakeExtensions(params string[] names)
    {
        foreach (var n in names) Directory.CreateDirectory(Path.Combine(_extRoot, n));
    }

    private static List<string> ExtensionGrantsAt(string json, string locationKey)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        var approvals = root["locations"]![locationKey]!["tool_approvals"]!.AsArray();
        var result = new List<string>();
        foreach (var n in approvals)
        {
            if (n is JsonObject o && o["kind"]?.GetValue<string>() == "extension-permission-access")
                result.Add(o["extensionName"]!.GetValue<string>());
        }
        return result;
    }

    [Fact]
    public void CreatesFile_AndGrantsAllUserExtensions()
    {
        MakeExtensions("adr-generator", "cloud-patterns");
        var dir = @"C:\repo\FabricPOCPortal";

        var added = NewSvc().EnsureExtensionGrants(dir);

        Assert.Equal(2, added);
        Assert.True(File.Exists(_permPath));
        var grants = ExtensionGrantsAt(File.ReadAllText(_permPath), dir);
        Assert.Contains("user:adr-generator", grants);
        Assert.Contains("user:cloud-patterns", grants);
    }

    [Fact]
    public void PreservesExistingApprovals_AndOtherLocations()
    {
        MakeExtensions("cloud-patterns");
        var dir = @"C:\repo\Proj";
        // Seed an existing config with a command approval + a project grant at
        // the SAME dir, plus an unrelated other location.
        var seed = new JsonObject
        {
            ["locations"] = new JsonObject
            {
                [dir] = new JsonObject
                {
                    ["tool_approvals"] = new JsonArray
                    {
                        new JsonObject { ["kind"] = "commands", ["commandIdentifiers"] = new JsonArray { "Remove-Item" } },
                        new JsonObject { ["kind"] = "extension-permission-access", ["extensionName"] = "project:pm-review" },
                    },
                },
                [@"C:\other"] = new JsonObject
                {
                    ["tool_approvals"] = new JsonArray { new JsonObject { ["kind"] = "write" } },
                },
            },
        };
        File.WriteAllText(_permPath, seed.ToJsonString());

        var added = NewSvc().EnsureExtensionGrants(dir);

        Assert.Equal(1, added);   // only cloud-patterns is new
        var json = File.ReadAllText(_permPath);
        var grants = ExtensionGrantsAt(json, dir);
        Assert.Contains("project:pm-review", grants);     // preserved
        Assert.Contains("user:cloud-patterns", grants);   // added
        // Command approval + other location preserved.
        var root = JsonNode.Parse(json)!.AsObject();
        Assert.True(root["locations"]!.AsObject().ContainsKey(@"C:\other"));
        var kinds = root["locations"]![dir]!["tool_approvals"]!.AsArray()
            .Select(n => n!["kind"]!.GetValue<string>()).ToList();
        Assert.Contains("commands", kinds);
    }

    [Fact]
    public void Idempotent_SecondCallAddsNothing()
    {
        MakeExtensions("adr-generator", "cloud-patterns");
        var dir = @"C:\repo\X";
        var svc = NewSvc();

        Assert.Equal(2, svc.EnsureExtensionGrants(dir));
        Assert.Equal(0, svc.EnsureExtensionGrants(dir));   // already granted

        var grants = ExtensionGrantsAt(File.ReadAllText(_permPath), dir);
        Assert.Equal(2, grants.Count);   // no duplicates
    }

    [Fact]
    public void BlankDirectory_ReturnsZero()
    {
        MakeExtensions("adr-generator");
        Assert.Equal(0, NewSvc().EnsureExtensionGrants("   "));
        Assert.False(File.Exists(_permPath));
    }

    [Fact]
    public void NoInstalledExtensions_ReturnsZero()
    {
        // _extRoot exists but is empty.
        Assert.Equal(0, NewSvc().EnsureExtensionGrants(@"C:\repo\X"));
        Assert.False(File.Exists(_permPath));
    }

    [Fact]
    public void UnparseableExistingFile_IsLeftUntouched()
    {
        MakeExtensions("adr-generator");
        File.WriteAllText(_permPath, "{ this is not valid json ");

        var added = NewSvc().EnsureExtensionGrants(@"C:\repo\X");

        Assert.Equal(0, added);
        Assert.Equal("{ this is not valid json ", File.ReadAllText(_permPath));   // unchanged
    }

    [Fact]
    public void MatchesExistingLocationKey_CaseInsensitively()
    {
        MakeExtensions("cloud-patterns");
        var existingKey = @"C:\Repo\Proj";
        var seed = new JsonObject
        {
            ["locations"] = new JsonObject
            {
                [existingKey] = new JsonObject { ["tool_approvals"] = new JsonArray() },
            },
        };
        File.WriteAllText(_permPath, seed.ToJsonString());

        // Call with a differently-cased + trailing-slash variant of the same dir.
        NewSvc().EnsureExtensionGrants(@"c:\repo\proj\");

        var root = JsonNode.Parse(File.ReadAllText(_permPath))!.AsObject();
        // Must reuse the existing key, not create a second location entry.
        Assert.Single(root["locations"]!.AsObject());
        var grants = ExtensionGrantsAt(File.ReadAllText(_permPath), existingKey);
        Assert.Contains("user:cloud-patterns", grants);
    }
}
