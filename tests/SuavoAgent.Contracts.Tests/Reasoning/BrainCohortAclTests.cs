using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;
using SuavoAgent.Contracts.Reasoning;
using SuavoAgent.Contracts.Security;
using Xunit;

namespace SuavoAgent.Contracts.Tests.Reasoning;

public sealed class BrainCohortAclTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "suavo-brain-acl-" + Guid.NewGuid().ToString("N"));

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Protected_cohort_gives_core_read_execute_but_no_persistent_write_authority()
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(Path.Combine(_root, "native"));
        File.WriteAllBytes(Path.Combine(_root, "native", "llama.dll"), [1, 2, 3]);

        var protectedResult = BrainCohortAcl.ProtectAndVerify(_root);

        Assert.True(protectedResult.IsValid, protectedResult.Code);
        var rules = new FileInfo(Path.Combine(_root, "native", "llama.dll"))
            .GetAccessControl(AccessControlSections.Access)
            .GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>();
        var core = Assert.Single(rules, rule =>
            rule.IdentityReference.Value == CoreServiceIdentity.ServiceSid);
        const FileSystemRights dangerous =
            FileSystemRights.WriteData |
            FileSystemRights.AppendData |
            FileSystemRights.Delete |
            FileSystemRights.ChangePermissions |
            FileSystemRights.TakeOwnership;
        Assert.Equal(FileSystemRights.ReadAndExecute,
            core.FileSystemRights & FileSystemRights.ReadAndExecute);
        Assert.Equal(0, (int)(core.FileSystemRights & dangerous));
    }

    [Fact]
    public void Added_core_modify_ace_is_detected_fail_closed()
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(_root);
        Assert.True(BrainCohortAcl.ProtectAndVerify(_root).IsValid);
        var directory = new DirectoryInfo(_root);
        var security = directory.GetAccessControl(AccessControlSections.Access);
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(CoreServiceIdentity.ServiceSid),
            FileSystemRights.Modify,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        directory.SetAccessControl(security);

        var result = BrainCohortAcl.Verify(_root);

        Assert.False(result.IsValid);
    }

    public void Dispose()
    {
        try
        {
            if (OperatingSystem.IsWindows() && Directory.Exists(_root))
            {
                var directory = new DirectoryInfo(_root);
                var security = directory.GetAccessControl();
                security.SetAccessRuleProtection(isProtected: false, preserveInheritance: true);
                directory.SetAccessControl(security);
            }
            Directory.Delete(_root, recursive: true);
        }
        catch { }
    }
}
