using SuavoAgent.Core.Compliance;
using Xunit;

namespace SuavoAgent.Core.Tests.Compliance;

public class CompliancePostureTests
{
    // ── Resolve ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("hipaa", ComplianceMode.Hipaa)]
    [InlineData("HIPAA", ComplianceMode.Hipaa)]
    [InlineData("none",  ComplianceMode.None)]
    [InlineData("pci",   ComplianceMode.Pci)]
    public void Resolve_known_strings(string input, ComplianceMode expected) =>
        Assert.Equal(expected, CompliancePosture.Resolve(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("minimal")]
    public void Resolve_unknown_or_null_fails_closed_to_hipaa(string? input) =>
        Assert.Equal(ComplianceMode.Hipaa, CompliancePosture.Resolve(input));

    // ── Enforce (anti-downgrade) ──────────────────────────────────────────────

    [Fact]
    public void Enforce_refuses_downgrade_from_hipaa_to_none() =>
        Assert.Equal(ComplianceMode.Hipaa,
            CompliancePosture.Enforce(ComplianceMode.None, ComplianceMode.Hipaa));

    [Fact]
    public void Enforce_allows_upgrade_from_none_to_hipaa() =>
        Assert.Equal(ComplianceMode.Hipaa,
            CompliancePosture.Enforce(ComplianceMode.Hipaa, ComplianceMode.None));

    [Fact]
    public void Enforce_same_level_returns_same() =>
        Assert.Equal(ComplianceMode.Hipaa,
            CompliancePosture.Enforce(ComplianceMode.Hipaa, ComplianceMode.Hipaa));

    [Fact]
    public void Enforce_incoming_pci_with_lkg_hipaa_returns_pci() =>
        Assert.Equal(ComplianceMode.Pci,
            CompliancePosture.Enforce(ComplianceMode.Pci, ComplianceMode.Hipaa));

    // ── LastKnownGoodStore ────────────────────────────────────────────────────

    [Fact]
    public void LkgStore_round_trips_mode()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            LastKnownGoodStore.Write(dir, ComplianceMode.Hipaa);
            var read = LastKnownGoodStore.TryRead(dir);
            Assert.Equal(ComplianceMode.Hipaa, read);
        }
        finally { System.IO.Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void LkgStore_absent_returns_null()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString());
        Assert.Null(LastKnownGoodStore.TryRead(dir));
    }

    [Fact]
    public void LkgStore_corrupt_file_returns_hipaa()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(
                System.IO.Path.Combine(dir, "vertical-compliance-lkg.json"),
                "CORRUPT-NOT-JSON");
            Assert.Equal(ComplianceMode.Hipaa, LastKnownGoodStore.TryRead(dir));
        }
        finally { System.IO.Directory.Delete(dir, recursive: true); }
    }

    // ── ConfigOverrideStore block paths ──────────────────────────────────────

    [Theory]
    [InlineData("Agent.ComplianceMode")]
    [InlineData("Agent.VerticalConfig")]
    [InlineData("Agent.SystemConnector")]
    public void ConfigOverrideStore_blocks_vertical_posture_paths(string path)
    {
        // Verify the paths are present in the blocked list via reflection.
        // We test behaviour, not implementation, but the simplest observable
        // behaviour is that Apply rejects these paths.
        var store = MakeStore();
        var overrides = new List<SuavoAgent.Contracts.Cloud.ConfigOverride>
        {
            new() { Path = path, Value = System.Text.Json.JsonDocument.Parse("\"injected\"").RootElement },
        };
        store.Apply(overrides);  // must not throw; the blocked path is silently dropped

        // The file is written only for non-blocked overrides — if written, it must not contain the blocked key.
        if (System.IO.File.Exists(_storePath))
        {
            var content = System.IO.File.ReadAllText(_storePath);
            Assert.DoesNotContain(path.Split('.')[^1], content);
        }
    }

    private string _storePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid() + ".json");
    private SuavoAgent.Core.Cloud.ConfigOverrideStore MakeStore() =>
        new(_storePath, Microsoft.Extensions.Logging.Abstractions.NullLogger<SuavoAgent.Core.Cloud.ConfigOverrideStore>.Instance);
}
