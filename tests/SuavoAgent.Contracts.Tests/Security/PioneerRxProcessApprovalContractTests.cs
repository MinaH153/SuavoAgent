using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Contracts.Security;
using Xunit;

namespace SuavoAgent.Contracts.Tests.Security;

public sealed class PioneerRxProcessApprovalContractTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);
    private const string PharmacyId = "11111111-1111-1111-1111-111111111111";
    private const string MachineId = "22222222-2222-2222-2222-222222222222";
    private static readonly string Hex = new('a', 64);
    private static readonly string SqlCertificateHex = new('b', 64);
    private static readonly string Signature = PioneerRxProcessApprovalContract.Base64UrlEncode(new byte[64]);

    [Fact]
    public void Receipt_RejectsUnsortedOrDuplicateBaaScopeSet()
    {
        var receipt = Receipt() with
        {
            ApprovedBaaScopeTags = new[] { "writeback", "read", "read" },
        };

        Assert.False(PioneerRxProcessApprovalContract.TryValidate(
            receipt, null, PharmacyId, MachineId, Hex, Now, out var code));
        Assert.Equal("approval_fields_invalid", code);
    }

    [Fact]
    public void Receipt_RejectsLifetimeBeyondThirtyDays()
    {
        var receipt = Receipt() with
        {
            ExpiresAtUtc = Utc(Now.AddDays(31)),
        };

        Assert.False(PioneerRxProcessApprovalContract.TryValidate(
            receipt, null, PharmacyId, MachineId, Hex, Now, out var code));
        Assert.Equal("approval_expired_or_time_invalid", code);
    }

    [Fact]
    public void Receipt_RejectsNonCanonicalUuidAndBase64UrlSignature()
    {
        var receipt = Receipt() with
        {
            ReceiptId = "AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA",
            MaintenanceSignature = Convert.ToBase64String(new byte[64]),
        };

        Assert.False(PioneerRxProcessApprovalContract.TryValidate(
            receipt, null, PharmacyId, MachineId, Hex, Now, out var code));
        Assert.Equal("approval_fields_invalid", code);
    }

    [Fact]
    public void AuthorityState_RejectsCounterOrReceiptRollbackBeforeSignatureCheck()
    {
        var receipt = Receipt();
        var state = new PioneerRxApprovalAuthorityState(
            PioneerRxProcessApprovalContract.CurrentSchemaVersion,
            PharmacyId,
            MachineId,
            "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
            receipt.ApprovalCounter - 1,
            Array.Empty<string>(),
            Utc(Now.AddMinutes(-1)),
            Utc(Now.AddHours(1)),
            RemoteCommandTrust.CommandV1KeyId,
            Signature);

        Assert.False(PioneerRxProcessApprovalContract.TryValidateAuthorityState(
            state, receipt, PharmacyId, MachineId, Now, out var code));
        Assert.Equal("approval_authority_counter_mismatch", code);
    }

    [Fact]
    public void Canonical_BindsCounterScopesAndCloudKey()
    {
        var receipt = Receipt();
        var canonical = PioneerRxProcessApprovalContract.Canonical(receipt);

        Assert.Contains("|7|read,writeback|", canonical, StringComparison.Ordinal);
        Assert.Contains($"|{SqlCertificateHex}|", canonical, StringComparison.Ordinal);
        Assert.EndsWith($"|{RemoteCommandTrust.CommandV1KeyId}", canonical, StringComparison.Ordinal);
    }

    [Fact]
    public void Canonical_V2GoldenFixture_IsByteStable()
    {
        var expected =
            $"suavo.pioneerrx-process-approval.v2|2|aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa|" +
            $"{PharmacyId}|{MachineId}|{Hex}|PioneerPharmacy.exe|" +
            @"C:\Program Files\PioneerRx\PioneerPharmacy.exe|" +
            $"{Hex}|CN=New Tech Computer Systems|{Hex}|PioneerRx|1.2.3.4|" +
            $"eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee|{SqlCertificateHex}|" +
            $"S-1-5-21-1-2-3-1001|{Hex}|{Hex}|7|read,writeback|" +
            $"2026-07-11T11:59:00.0000000Z|2026-07-12T12:00:00.0000000Z||" +
            RemoteCommandTrust.CommandV1KeyId;

        Assert.Equal(expected, PioneerRxProcessApprovalContract.Canonical(Receipt()));
    }

    private static PioneerRxProcessApprovalReceipt Receipt() => new(
        PioneerRxProcessApprovalContract.CurrentSchemaVersion,
        "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
        PharmacyId,
        MachineId,
        Hex,
        Convert.ToBase64String(new byte[91]),
        "PioneerPharmacy.exe",
        @"C:\Program Files\PioneerRx\PioneerPharmacy.exe",
        Hex,
        "CN=New Tech Computer Systems",
        Hex,
        "PioneerRx",
        "1.2.3.4",
        "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee",
        SqlCertificateHex,
        "S-1-5-21-1-2-3-1001",
        Hex,
        Hex,
        7,
        new[] { "read", "writeback" },
        Utc(Now.AddMinutes(-1)),
        Utc(Now.AddDays(1)),
        null,
        RemoteCommandTrust.CommandV1KeyId,
        Signature,
        Signature);

    private static string Utc(DateTimeOffset value) =>
        value.UtcDateTime.ToString(
            PioneerRxProcessApprovalContract.UtcTimestampFormat,
            System.Globalization.CultureInfo.InvariantCulture);
}
