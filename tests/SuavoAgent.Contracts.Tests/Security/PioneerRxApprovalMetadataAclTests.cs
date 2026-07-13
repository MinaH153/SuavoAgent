using System.Security.AccessControl;
using System.Security.Principal;
using SuavoAgent.Contracts.Security;
using Xunit;

namespace SuavoAgent.Contracts.Tests.Security;

public sealed class PioneerRxApprovalMetadataAclTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "suavo-pioneerrx-acl-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Exact_metadata_and_high_water_acls_reject_extra_write_authority()
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(_root);
        var metadata = Path.Combine(_root, "metadata.json");
        var highWater = Path.Combine(_root, "high-water.json");
        File.WriteAllText(metadata, "{}");
        File.WriteAllText(highWater, "{}");

        PioneerRxApprovalMetadataAcl.ProtectDirectory(_root);
        PioneerRxApprovalMetadataAcl.ProtectMetadataFile(metadata);
        PioneerRxApprovalMetadataAcl.ProtectHighWaterFile(highWater);

        Assert.True(PioneerRxApprovalMetadataAcl.ValidateDirectory(_root));
        Assert.True(PioneerRxApprovalMetadataAcl.ValidateFile(metadata, interactiveRead: true));
        Assert.True(PioneerRxApprovalMetadataAcl.ValidateFile(highWater, interactiveRead: false));

        var file = new FileInfo(metadata);
        var security = file.GetAccessControl(AccessControlSections.Access);
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier("S-1-5-32-545"),
            FileSystemRights.Modify,
            AccessControlType.Allow));
        file.SetAccessControl(security);

        Assert.False(PioneerRxApprovalMetadataAcl.ValidateFile(metadata, interactiveRead: true));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
