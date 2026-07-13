using System.Text.Json;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Contracts.Security;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.Workers;

public sealed class PioneerRxApprovalInstallCommandTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "suavo-pioneerrx-approval-command-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ExactCommand_ParsesAndExtraOrDuplicateFieldsFailClosed()
    {
        var data = JsonSerializer.SerializeToElement(new
        {
            schemaVersion = 1,
            protocolEpoch = PioneerRxApprovalMaintenanceContract.CurrentProtocolEpoch,
            commandId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
            receipt = Receipt(),
            authority = Authority(),
            vendorCatalog = Catalog(),
        });

        Assert.True(PioneerRxApprovalInstallCommandContract.TryParse(
            data, out var parsed, out var code));
        Assert.Equal("valid", code);
        Assert.NotNull(parsed);
        Assert.Equal(64, parsed!.PayloadDigest.Length);

        var extra = JsonSerializer.SerializeToElement(new
        {
            schemaVersion = 1,
            protocolEpoch = PioneerRxApprovalMaintenanceContract.CurrentProtocolEpoch,
            commandId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
            receipt = Receipt(),
            authority = Authority(),
            vendorCatalog = Catalog(),
            allowBypass = true,
        });
        Assert.False(PioneerRxApprovalInstallCommandContract.TryParse(
            extra, out _, out _));

        var raw = data.GetRawText().Replace(
            "\"schemaVersion\":1,",
            "\"schemaVersion\":1,\"schemaVersion\":1,",
            StringComparison.Ordinal);
        using var duplicate = JsonDocument.Parse(raw);
        Assert.False(PioneerRxApprovalInstallCommandContract.TryParse(
            duplicate.RootElement, out _, out _));
    }

    [Fact]
    public void Core_StagesOnlyAndRequiresExactSystemCompletion()
    {
        Directory.CreateDirectory(_directory);
        var command = Command();
        var requestPath = Path.Combine(_directory, "request.json");
        var completionPath = Path.Combine(_directory, "completion.json");

        var staged = PioneerRxApprovalInstallStager.Stage(command, requestPath);

        Assert.True(staged.Succeeded);
        Assert.True(File.Exists(requestPath));
        Assert.False(PioneerRxApprovalInstallStager.HasExactCompletion(
            command,
            out var pending,
            completionPath,
            requireProductionAcl: false));
        Assert.Equal("pending_system_install", pending);

        var completion = new PioneerRxApprovalInstallCompletion(
            PioneerRxApprovalMaintenanceContract.SchemaVersion,
            PioneerRxApprovalMaintenanceContract.CurrentProtocolEpoch,
            command.CommandId,
            command.PayloadDigest,
            command.Receipt.ApprovalCounter,
            command.Receipt.ReceiptId,
            PioneerRxApprovalMaintenanceContract.InstalledOutcome,
            "2026-07-11T12:01:00.0000000Z");
        File.WriteAllText(
            completionPath,
            JsonSerializer.Serialize(
                completion,
                PioneerRxApprovalMaintenanceContract.JsonOptions));

        Assert.True(PioneerRxApprovalInstallStager.HasExactCompletion(
            command,
            out var installed,
            completionPath,
            requireProductionAcl: false));
        Assert.Equal(PioneerRxApprovalMaintenanceContract.InstalledOutcome, installed);
    }

    private static PioneerRxApprovalInstallCommand Command()
    {
        const string commandId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
        var receipt = Receipt();
        var authority = Authority();
        var catalog = Catalog();
        return new(
            commandId,
            PioneerRxApprovalMaintenanceContract.CurrentProtocolEpoch,
            receipt,
            authority,
            catalog,
            PioneerRxApprovalMaintenanceContract.ComputePayloadDigest(
                commandId,
                receipt,
                authority,
                catalog));
    }

    private static PioneerRxProcessApprovalReceipt Receipt() => new(
        PioneerRxProcessApprovalContract.CurrentSchemaVersion,
        "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",
        "cccccccc-cccc-4ccc-8ccc-cccccccccccc",
        "dddddddd-dddd-4ddd-8ddd-dddddddddddd",
        new string('a', 64),
        Convert.ToBase64String(new byte[91]),
        "PioneerPharmacy.exe",
        @"C:\Program Files\PioneerRx\PioneerPharmacy.exe",
        new string('a', 64),
        "CN=PioneerRx",
        new string('a', 64),
        "PioneerRx",
        "1.2.3.4",
        "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee",
        new string('b', 64),
        "S-1-5-21-1-2-3-1001",
        new string('c', 64),
        new string('d', 64),
        7,
        ["read"],
        "2026-07-11T12:00:00.0000000Z",
        "2026-07-12T12:00:00.0000000Z",
        null,
        RemoteCommandTrust.CommandV1KeyId,
        new string('s', 86),
        new string('d', 86));

    private static PioneerRxApprovalAuthorityState Authority() => new(
        PioneerRxProcessApprovalContract.CurrentSchemaVersion,
        "cccccccc-cccc-4ccc-8ccc-cccccccccccc",
        "dddddddd-dddd-4ddd-8ddd-dddddddddddd",
        "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",
        7,
        [],
        "2026-07-11T12:00:00.0000000Z",
        "2026-07-11T13:00:00.0000000Z",
        RemoteCommandTrust.CommandV1KeyId,
        new string('s', 86));

    private static PioneerRxVendorIdentityCatalog Catalog() => new(
        1,
        "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee",
        [new PioneerRxVendorIdentityEntry(
            "ffffffff-ffff-4fff-8fff-ffffffffffff",
            "PioneerPharmacy.exe",
            "PioneerRx",
            "CN=PioneerRx",
            new string('a', 64),
            [@"C:\Program Files\PioneerRx\"],
            ["1.2.3.4"])],
        "2026-07-11T12:00:00.0000000Z",
        "2026-07-12T12:00:00.0000000Z",
        RemoteCommandTrust.CommandV1KeyId,
        new string('s', 86));

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); } catch { }
    }
}
