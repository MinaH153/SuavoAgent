using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;
using Microsoft.Win32;
using SuavoAgent.Contracts.Security;
using SuavoAgent.Contracts.Vision;
using Xunit;

namespace SuavoAgent.Contracts.Tests.Security;

public sealed class VisionRegistryAuthorityTests
{
    [Fact]
    public void Authority_identity_is_one_fixed_machine_value()
    {
        Assert.Equal(@"SOFTWARE\SuavoAgent\Vision", VisionRegistryAuthority.KeyPath);
        Assert.Equal("State", VisionRegistryAuthority.ValueName);
    }

    [Fact]
    public void Non_windows_read_is_explicit_default_disabled_missing_state()
    {
        if (OperatingSystem.IsWindows()) return;

        var result = VisionRegistryAuthority.ReadState();

        Assert.Equal(VisionRegistryReadStatus.Missing, result.Status);
        Assert.Equal("vision_registry_non_windows_default_disabled", result.Code);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Windows_acl_is_protected_system_owned_and_exact()
    {
        if (!OperatingSystem.IsWindows()) return;

        var security = VisionRegistryAuthority.CreateExactSecurity();
        var binary = security.GetSecurityDescriptorBinaryForm();

        Assert.True(
            VisionRegistryAuthority.VerifySecurityDescriptor(binary, out var code),
            code);
        var raw = new RawSecurityDescriptor(binary, 0);
        Assert.Equal("S-1-5-18", raw.Owner?.Value);
        Assert.True(raw.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected));
        var rules = raw.DiscretionaryAcl!
            .OfType<CommonAce>()
            .ToDictionary(
                ace => ace.SecurityIdentifier.Value,
                ace => (RegistryRights)ace.AccessMask,
                StringComparer.Ordinal);
        Assert.Equal(RegistryRights.FullControl, rules["S-1-5-18"]);
        Assert.Equal(RegistryRights.FullControl, rules["S-1-5-32-544"]);
        Assert.Equal(RegistryRights.ReadKey, rules["S-1-5-32-545"]);
        Assert.Equal(VisionRegistryAuthority.CoreRights, rules[CoreServiceIdentity.ServiceSid]);
        Assert.False((rules[CoreServiceIdentity.ServiceSid] & RegistryRights.CreateSubKey) != 0);
        Assert.False((rules[CoreServiceIdentity.ServiceSid] & RegistryRights.ChangePermissions) != 0);
        Assert.False((rules[CoreServiceIdentity.ServiceSid] & RegistryRights.TakeOwnership) != 0);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Windows_acl_verifier_rejects_an_extra_writer()
    {
        if (!OperatingSystem.IsWindows()) return;
        var security = VisionRegistryAuthority.CreateExactSecurity();
        security.AddAccessRule(new RegistryAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            RegistryRights.SetValue,
            AccessControlType.Allow));

        Assert.False(VisionRegistryAuthority.VerifySecurityDescriptor(
            security.GetSecurityDescriptorBinaryForm(),
            out _));
    }

    [Fact]
    public void Malformed_state_under_exact_acl_is_quarantined_and_cleared()
    {
        var result = VisionRegistryAuthority.EvaluateStateForRepair(
            aclWasExact: true,
            stateExisted: true,
            stateWasString: true,
            state: "{}",
            dataDirectory: Path.GetTempPath());

        Assert.False(result.StatePreserved);
        Assert.True(result.StateCleared);
        Assert.Equal("vision_registry_invalid_state_quarantined", result.Code);
        Assert.Matches("^[0-9a-f]{64}$", result.InvalidStateSha256);
    }

    [Fact]
    public void Valid_state_under_exact_acl_survives_ordinary_repair()
    {
        const string commandId = "11111111-1111-4111-8111-111111111111";
        var root = Path.Combine(Path.GetTempPath(), "suavo-vision-repair-valid");
        var state = VisionConfigurationStateCodec.Create(
            9,
            commandId,
            new DateTimeOffset(2026, 7, 11, 0, 0, 0, TimeSpan.Zero),
            VisionOptionsSnapshot.DisabledDefault(),
            root);
        var json = VisionConfigurationStateCodec.Serialize(state, root);

        var result = VisionRegistryAuthority.EvaluateStateForRepair(
            aclWasExact: true,
            stateExisted: true,
            stateWasString: true,
            state: json,
            dataDirectory: root);

        Assert.True(result.StatePreserved);
        Assert.False(result.StateCleared);
        Assert.Equal("vision_registry_valid_state_preserved", result.Code);
    }

    [Fact]
    public void State_written_before_acl_hardening_is_never_preserved()
    {
        var result = VisionRegistryAuthority.EvaluateStateForRepair(
            aclWasExact: false,
            stateExisted: true,
            stateWasString: true,
            state: "syntactically irrelevant",
            dataDirectory: Path.GetTempPath());

        Assert.False(result.StatePreserved);
        Assert.True(result.StateCleared);
        Assert.Equal("vision_registry_untrusted_acl_state_quarantined", result.Code);
    }

    [Fact]
    public void Failed_repair_receipt_blocks_the_destructive_clear_boundary()
    {
        var disposition = new VisionRegistryProvisionResult(
            StatePreserved: false,
            StateCleared: true,
            Code: "vision_registry_invalid_state_quarantined",
            InvalidStateSha256: new string('a', 64));
        var called = false;

        var allowed = VisionRegistryAuthority.TryPersistRepairEvidence(
            disposition,
            result =>
            {
                called = true;
                Assert.Same(disposition, result);
                return false;
            });

        Assert.True(called);
        Assert.False(allowed);
        Assert.False(VisionRegistryAuthority.TryPersistRepairEvidence(
            disposition,
            persistBeforeClear: null));
    }
}
