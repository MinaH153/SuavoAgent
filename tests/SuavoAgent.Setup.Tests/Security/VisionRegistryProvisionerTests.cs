using SuavoAgent.Setup.Security;
using SuavoAgent.Contracts.Security;
using System.Text.Json;
using Xunit;

namespace SuavoAgent.Setup.Tests.Security;

public sealed class VisionRegistryProvisionerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "suavo-vision-retire-" + Guid.NewGuid().ToString("N"));

    public VisionRegistryProvisionerTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Missing_legacy_file_is_already_retired()
    {
        Assert.True(VisionRegistryProvisioner.RetireLegacyConfig(_root));
    }

    [Fact]
    public void Exact_regular_legacy_file_is_deleted()
    {
        var path = Path.Combine(_root, "vision.json");
        File.WriteAllText(path, "{\"Enabled\":true}");

        Assert.True(VisionRegistryProvisioner.RetireLegacyConfig(_root));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Directory_named_like_legacy_file_is_rejected_and_preserved()
    {
        var path = Path.Combine(_root, "vision.json");
        Directory.CreateDirectory(path);

        Assert.False(VisionRegistryProvisioner.RetireLegacyConfig(_root));
        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public void Symlink_named_like_legacy_file_is_rejected_without_following_target()
    {
        var target = Path.Combine(_root, "target.json");
        var link = Path.Combine(_root, "vision.json");
        File.WriteAllText(target, "must-survive");
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (exception is
                   UnauthorizedAccessException or PlatformNotSupportedException or IOException)
        {
            return;
        }

        Assert.False(VisionRegistryProvisioner.RetireLegacyConfig(_root));
        Assert.Equal("must-survive", File.ReadAllText(target));
    }

    [Fact]
    public void Cleared_invalid_state_writes_a_durable_visible_repair_receipt()
    {
        var result = new VisionRegistryProvisionResult(
            StatePreserved: false,
            StateCleared: true,
            Code: "vision_registry_invalid_state_quarantined",
            InvalidStateSha256: new string('a', 64));

        Assert.True(VisionRegistryProvisioner.WriteRepairReceipt(_root, result));

        var path = Path.Combine(_root, "vision-registry-repair.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(
            "repaired_default_disabled",
            document.RootElement.GetProperty("status").GetString());
        Assert.Equal(result.Code, document.RootElement.GetProperty("repairCode").GetString());
        Assert.Equal(
            result.InvalidStateSha256,
            document.RootElement.GetProperty("invalidStateSha256").GetString());
    }

    [Fact]
    public void Install_and_repair_cohort_hook_propagates_a_privileged_provision_failure()
    {
        var aclCalled = false;

        var result = VisionRegistryProvisioner.ProvisionReleaseCohorts(
            _root,
            _ =>
            {
                aclCalled = true;
                return true;
            },
            (root, acl, _) =>
            {
                Assert.Equal(_root, root);
                Assert.True(acl("cohort"));
                return Task.FromResult(new ReleaseOcrProvisionResult(
                    false,
                    "vision_release_cohort_bundle_mismatch"));
            });

        Assert.True(aclCalled);
        Assert.False(result.Succeeded);
        Assert.Equal("vision_release_cohort_bundle_mismatch", result.Code);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
