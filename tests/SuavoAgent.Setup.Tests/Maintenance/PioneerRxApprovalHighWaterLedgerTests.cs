using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Contracts.Security;
using SuavoAgent.Setup.Maintenance;
using Xunit;

namespace SuavoAgent.Setup.Tests.Maintenance;

public sealed class PioneerRxApprovalHighWaterLedgerTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "suavo-pioneerrx-high-water-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Same_artifacts_under_a_new_delivery_command_are_an_exact_replay()
    {
        var ledger = Ledger();
        var first = Request(commandId: "11111111-1111-1111-1111-111111111111");
        var initial = ledger.Evaluate(first, Now);
        Assert.Equal(PioneerRxHighWaterDecisionKind.Advance, initial.Kind);
        ledger.Commit(initial.Proposed);

        var duplicate = Request(
            commandId: "22222222-2222-2222-2222-222222222222");
        var replay = ledger.Evaluate(duplicate, Now.AddMinutes(1));

        Assert.Equal(PioneerRxHighWaterDecisionKind.ExactReplay, replay.Kind);
        Assert.Equal(duplicate.CommandId, replay.Proposed.CommandId);
        Assert.Equal(duplicate.PayloadDigest, replay.Proposed.PayloadDigest);
        ledger.Commit(replay.Proposed);
        Assert.Equal(duplicate.CommandId, ledger.Read()!.CommandId);
    }

    [Fact]
    public void Same_counter_newer_revocation_advances_and_blocks_old_authority_replay()
    {
        var ledger = Ledger();
        var approved = Request();
        var initial = ledger.Evaluate(approved, Now);
        ledger.Commit(initial.Proposed);

        var revoked = Request(
            authorityIssuedAt: Now.AddMinutes(2),
            revoked: true,
            commandId: "22222222-2222-2222-2222-222222222222");
        var revocation = ledger.Evaluate(revoked, Now.AddMinutes(2));

        Assert.Equal(PioneerRxHighWaterDecisionKind.Advance, revocation.Kind);
        Assert.True(revocation.Proposed.Revoked);
        ledger.Commit(revocation.Proposed);

        var rollback = ledger.Evaluate(approved, Now.AddMinutes(3));
        Assert.Equal(PioneerRxHighWaterDecisionKind.Rollback, rollback.Kind);
        Assert.Equal("approval_authority_rollback", rollback.Code);
    }

    [Fact]
    public void Same_counter_cannot_change_receipt_or_catalog()
    {
        var ledger = Ledger();
        var initial = ledger.Evaluate(Request(), Now);
        ledger.Commit(initial.Proposed);

        var differentReceipt = ledger.Evaluate(
            Request(receiptId: "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            Now.AddMinutes(1));
        var differentCatalog = ledger.Evaluate(
            Request(catalogId: "cccccccc-cccc-cccc-cccc-cccccccccccc"),
            Now.AddMinutes(1));

        Assert.Equal(PioneerRxHighWaterDecisionKind.Conflict, differentReceipt.Kind);
        Assert.Equal("approval_generation_conflict", differentReceipt.Code);
        Assert.Equal(PioneerRxHighWaterDecisionKind.Conflict, differentCatalog.Kind);
        Assert.Equal("approval_generation_conflict", differentCatalog.Code);
    }

    [Fact]
    public void Same_issue_time_with_different_authority_is_a_conflict()
    {
        var ledger = Ledger();
        var initial = ledger.Evaluate(Request(), Now);
        ledger.Commit(initial.Proposed);

        var changed = Request(authoritySignature: new string('z', 86));
        var decision = ledger.Evaluate(changed, Now.AddMinutes(1));

        Assert.Equal(PioneerRxHighWaterDecisionKind.Conflict, decision.Kind);
        Assert.Equal("approval_authority_conflict", decision.Code);
    }

    private PioneerRxApprovalHighWaterLedger Ledger()
    {
        Directory.CreateDirectory(_root);
        return new PioneerRxApprovalHighWaterLedger(
            Path.Combine(_root, PioneerRxApprovalMaintenanceContract.HighWaterFileName),
            protect: _ => { },
            validate: File.Exists);
    }

    private static PioneerRxApprovalInstallRequest Request(
        string receiptId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
        string catalogId = "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee",
        DateTimeOffset? authorityIssuedAt = null,
        bool revoked = false,
        string commandId = "11111111-1111-1111-1111-111111111111",
        string? authoritySignature = null)
    {
        var receipt = Receipt(receiptId, catalogId);
        var authority = Authority(
            receipt,
            authorityIssuedAt ?? Now,
            revoked,
            authoritySignature ?? new string('s', 86));
        var catalog = Catalog(catalogId);
        return new PioneerRxApprovalInstallRequest(
            PioneerRxApprovalMaintenanceContract.SchemaVersion,
            PioneerRxApprovalMaintenanceContract.CurrentProtocolEpoch,
            commandId,
            PioneerRxApprovalMaintenanceContract.ComputePayloadDigest(
                commandId,
                receipt,
                authority,
                catalog),
            receipt,
            authority,
            catalog,
            Utc(Now));
    }

    private static PioneerRxProcessApprovalReceipt Receipt(
        string receiptId,
        string catalogId) => new(
        PioneerRxProcessApprovalContract.CurrentSchemaVersion,
        receiptId,
        "33333333-3333-3333-3333-333333333333",
        "44444444-4444-4444-4444-444444444444",
        new string('a', 64),
        Convert.ToBase64String(new byte[91]),
        "PioneerPharmacy.exe",
        @"C:\Program Files\PioneerRx\PioneerPharmacy.exe",
        new string('b', 64),
        "CN=New Tech Computer Systems",
        new string('c', 64),
        "PioneerRx",
        "1.2.3.4",
        catalogId,
        new string('d', 64),
        "S-1-5-21-1-2-3-1001",
        new string('e', 64),
        new string('f', 64),
        7,
        new[] { "read" },
        Utc(Now.AddMinutes(-1)),
        Utc(Now.AddDays(1)),
        null,
        RemoteCommandTrust.CommandV1KeyId,
        new string('q', 86),
        new string('r', 86));

    private static PioneerRxApprovalAuthorityState Authority(
        PioneerRxProcessApprovalReceipt receipt,
        DateTimeOffset issuedAt,
        bool revoked,
        string signature) => new(
        PioneerRxProcessApprovalContract.CurrentSchemaVersion,
        receipt.PharmacyId,
        receipt.MachineFingerprint,
        receipt.ReceiptId,
        receipt.ApprovalCounter,
        revoked ? new[] { receipt.ReceiptId } : Array.Empty<string>(),
        Utc(issuedAt),
        Utc(issuedAt.AddHours(1)),
        RemoteCommandTrust.CommandV1KeyId,
        signature);

    private static PioneerRxVendorIdentityCatalog Catalog(string catalogId) => new(
        PioneerRxVendorIdentityCatalogContract.SchemaVersion,
        catalogId,
        new[]
        {
            new PioneerRxVendorIdentityEntry(
                "ffffffff-ffff-4fff-8fff-ffffffffffff",
                "PioneerPharmacy.exe",
                "PioneerRx",
                "CN=New Tech Computer Systems",
                new string('c', 64),
                new[] { @"C:\Program Files\PioneerRx\" },
                new[] { "1.2.3.4" }),
        },
        Utc(Now.AddMinutes(-1)),
        Utc(Now.AddDays(30)),
        RemoteCommandTrust.CommandV1KeyId,
        new string('t', 86));

    private static string Utc(DateTimeOffset value) =>
        value.UtcDateTime.ToString(
            PioneerRxProcessApprovalContract.UtcTimestampFormat,
            System.Globalization.CultureInfo.InvariantCulture);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
