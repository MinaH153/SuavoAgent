using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using SuavoAgent.Contracts.Security;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.State;

public sealed class InstalledDataRootVerifierTests
{
    [Fact]
    public void Missing_root_is_never_treated_as_a_safe_installed_boundary() =>
        Assert.False(InstalledDataRootVerifier.IsSafe(Path.Combine(
            Path.GetTempPath(),
            "missing-suavo-root-" + Guid.NewGuid().ToString("N"))));

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Exact_acl_requires_system_owner_and_only_the_four_reviewed_rules()
    {
        if (!OperatingSystem.IsWindows()) return;
        var exact = Descriptor(owner: "S-1-5-18");
        var wrongOwner = Descriptor(owner: "S-1-5-32-544");

        Assert.True(InstalledDataRootVerifier.VerifyDescriptor(exact));
        Assert.False(InstalledDataRootVerifier.VerifyDescriptor(wrongOwner));
    }

    [SupportedOSPlatform("windows")]
    private static byte[] Descriptor(string owner)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(new SecurityIdentifier(owner));
        var inherited = InheritanceFlags.ContainerInherit |
                        InheritanceFlags.ObjectInherit;
        Add("S-1-5-18", FileSystemRights.FullControl, inherited);
        Add("S-1-5-32-544", FileSystemRights.FullControl, inherited);
        Add(CoreServiceIdentity.ServiceSid, FileSystemRights.Modify, inherited);
        Add("S-1-5-32-545", FileSystemRights.ReadAndExecute, InheritanceFlags.None);
        return security.GetSecurityDescriptorBinaryForm();

        void Add(string sid, FileSystemRights rights, InheritanceFlags inheritance) =>
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(sid),
                rights,
                inheritance,
                PropagationFlags.None,
                AccessControlType.Allow));
    }
}
