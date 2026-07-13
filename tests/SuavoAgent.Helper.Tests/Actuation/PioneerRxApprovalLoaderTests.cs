using System.Text;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Contracts.Security;
using SuavoAgent.Helper.Actuation;
using Xunit;

namespace SuavoAgent.Helper.Tests.Actuation;

public sealed class PioneerRxApprovalLoaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"suavo-pioneer-{Guid.NewGuid():N}");

    public PioneerRxApprovalLoaderTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    [Fact]
    public void StrictParser_RejectsUnknownRootProperty()
    {
        var path = Path.Combine(_directory, "receipt.json");
        File.WriteAllText(path, "{\"schemaVersion\":1,\"unexpected\":true}");

        Assert.False(PioneerRxProcessApprovalLoader.TryReadStrictJson(
            path, 4096, out PioneerRxProcessApprovalReceipt? _));
    }

    [Fact]
    public void StrictParser_RejectsCaseVariantDuplicateProperty()
    {
        var path = Path.Combine(_directory, "receipt.json");
        File.WriteAllText(path, "{\"schemaVersion\":1,\"SchemaVersion\":1}");

        Assert.False(PioneerRxProcessApprovalLoader.TryReadStrictJson(
            path, 4096, out PioneerRxProcessApprovalReceipt? _));
    }

    [Fact]
    public void StrictParser_RejectsNestedDuplicateProperty()
    {
        var path = Path.Combine(_directory, "projection.json");
        File.WriteAllText(path, "{\"outer\":{\"value\":1,\"value\":2}}");

        Assert.False(PioneerRxProcessApprovalLoader.TryReadStrictJson(
            path,
            4096,
            out Dictionary<string, Dictionary<string, int>>? _));
    }

    [Fact]
    public void StrictParser_RejectsOversizedReceiptBeforeDeserialization()
    {
        var path = Path.Combine(_directory, "receipt.json");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(new string('x', 4097)));

        Assert.False(PioneerRxProcessApprovalLoader.TryReadStrictJson(
            path, 4096, out PioneerRxProcessApprovalReceipt? _));
    }

    [Fact]
    public void InstalledIdentity_RequiresDeviceKeyBindingAndUniqueAgentProperties()
    {
        var path = Path.Combine(_directory, "appsettings.json");
        File.WriteAllText(path,
            "{\"Agent\":{\"PharmacyId\":\"p\",\"MachineFingerprint\":\"m\",\"DeviceAttestationKeyId\":\"" +
            new string('a', 64) + "\",\"pharmacyId\":\"override\"}}}");

        Assert.False(PioneerRxProcessApprovalLoader.TryReadInstalledIdentity(
            path, out _, out _, out _));
    }

    [Fact]
    public void DeniedTrustHasNoScopesAndCannotVerifyAnyPid()
    {
        var trust = new PioneerRxProcessTrustVerifier(
            PioneerRxApprovalLoadResult.Denied("approval_missing"));

        Assert.Empty(trust.ApprovedBaaScopeTags);
        Assert.False(trust.VerifyResolvedProcess(1).Trusted);
    }

    [Fact]
    public void Every_resolved_process_check_refreshes_live_authority_and_honors_revocation()
    {
        var refreshes = 0;
        var trust = new PioneerRxProcessTrustVerifier(
            new PioneerRxApprovalLoadResult(true, "approved", Receipt()),
            () =>
            {
                refreshes++;
                return PioneerRxApprovalLoadResult.Denied("approval_revoked");
            });

        var verdict = trust.VerifyResolvedProcess(123);

        Assert.False(verdict.Trusted);
        Assert.Equal("approval_revoked", verdict.Code);
        Assert.Equal(1, refreshes);
    }

    [Fact]
    public void Approval_installed_after_helper_start_becomes_visible_without_restart()
    {
        var receipt = Receipt();
        var trust = new PioneerRxProcessTrustVerifier(
            PioneerRxApprovalLoadResult.Denied("approval_missing"),
            () => new PioneerRxApprovalLoadResult(true, "approved", receipt));

        Assert.True(trust.IsApproved);
        Assert.Equal("approved", trust.ApprovalCode);
        Assert.Equal(receipt.ProcessName, trust.ApprovedProcessName);
        Assert.Contains("read", trust.ApprovedBaaScopeTags);
    }

    private static PioneerRxProcessApprovalReceipt Receipt() => new(
        PioneerRxProcessApprovalContract.CurrentSchemaVersion,
        "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
        "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",
        "cccccccc-cccc-4ccc-8ccc-cccccccccccc",
        new string('a', 64),
        Convert.ToBase64String(new byte[91]),
        "PioneerPharmacy.exe",
        @"C:\Program Files\PioneerRx\PioneerPharmacy.exe",
        new string('b', 64),
        "CN=New Tech Computer Systems",
        new string('c', 64),
        "PioneerRx",
        "1.2.3.4",
        "dddddddd-dddd-4ddd-8ddd-dddddddddddd",
        new string('d', 64),
        "S-1-5-21-1-2-3-1001",
        new string('e', 64),
        new string('f', 64),
        1,
        new[] { "read" },
        "2026-07-11T11:00:00.0000000Z",
        "2026-07-12T11:00:00.0000000Z",
        null,
        RemoteCommandTrust.CommandV1KeyId,
        new string('q', 86),
        new string('r', 86));
}
