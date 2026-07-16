using System.Text.Json;
using Xunit;

namespace SuavoAgent.Helper.Tests.SystemObservers;

public sealed class BrowserExtensionAssetTests
{
    [Fact]
    public void Manifest_RequestsOnlyTabsAndNativeMessaging()
    {
        var root = FindRepositoryRoot();
        using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "browser-extension", "manifest.json")));
        var manifest = document.RootElement;

        var permissions = manifest.GetProperty("permissions")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToHashSet(StringComparer.Ordinal);
        Assert.True(permissions.SetEquals(["nativeMessaging", "tabs"]));
        Assert.False(manifest.TryGetProperty("host_permissions", out _));
        Assert.False(manifest.TryGetProperty("content_scripts", out _));
        Assert.False(manifest.TryGetProperty("web_accessible_resources", out _));
    }

    [Fact]
    public void ServiceWorker_SendsHostnameAndContainsNoContentOrNetworkSurface()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "browser-extension",
            "service-worker.js"));

        Assert.Contains("hostname", source, StringComparison.Ordinal);
        Assert.Contains("active_tab_hostname", source, StringComparison.Ordinal);
        Assert.Contains("new URL(url)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("tab.title", source, StringComparison.Ordinal);
        Assert.DoesNotContain("chrome.history", source, StringComparison.Ordinal);
        Assert.DoesNotContain("chrome.cookies", source, StringComparison.Ordinal);
        Assert.DoesNotContain("fetch(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("XMLHttpRequest", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("native-host.chrome.json.template", "__PUBLISHED_CHROME_EXTENSION_ID__")]
    [InlineData("native-host.edge.json.template", "__PUBLISHED_EDGE_EXTENSION_ID__")]
    public void NativeHostManifest_RemainsNonDeployableUntilPublishedIdExists(
        string fileName,
        string placeholder)
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "browser-extension",
            fileName));

        Assert.Contains(placeholder, source, StringComparison.Ordinal);
        Assert.Contains("allowed_origins", source, StringComparison.Ordinal);
        Assert.Contains("__ABSOLUTE_SIGNED_NATIVE_HOST_PATH__", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ConnectorAuthorityTemplate_RequiresSchemaV2DeviceBrowserPaths()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "browser-extension",
            "connector-authority.json.template")));
        var root = document.RootElement;

        Assert.Equal(2, root.GetProperty("schemaVersion").GetInt32());
        var entries = root.GetProperty("allowedExtensions").EnumerateArray().ToArray();
        Assert.Equal(2, entries.Length);
        Assert.All(entries, entry => Assert.True(
            entry.TryGetProperty("browserExecutablePath", out _)));
        Assert.Contains(
            "__CANONICAL_PROTECTED_MACHINE_CHROME_EXE_PATH__",
            entries[0].GetProperty("browserExecutablePath").GetString());
        Assert.Contains(
            "__CANONICAL_PROTECTED_MACHINE_EDGE_EXE_PATH__",
            entries[1].GetProperty("browserExecutablePath").GetString());
    }

    private static string FindRepositoryRoot()
    {
        var cursor = new DirectoryInfo(AppContext.BaseDirectory);
        while (cursor is not null)
        {
            if (Directory.Exists(Path.Combine(cursor.FullName, "browser-extension")))
                return cursor.FullName;
            cursor = cursor.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate browser-extension assets.");
    }
}
