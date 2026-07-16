using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Watchdog;
using Xunit;

namespace SuavoAgent.Watchdog.Tests;

public sealed class RemoteRepairHandoffTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 10, 22, 0, 0, TimeSpan.Zero);
    private const string AgentId = "agent-remote-repair-test";
    private const string Fingerprint = "fingerprint-remote-repair-test";
    private const string KeyId = "remote-repair-test-key";
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "suavo-remote-repair-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private string RequestPath => Path.Combine(_root, "incoming", RemoteRepairContract.RequestFileName);
    private string LedgerPath => Path.Combine(_root, "maintenance", RemoteRepairContract.ReplayLedgerFileName);
    private string TelemetryPath => Path.Combine(_root, "telemetry", "watchdog.json");

    [Fact]
    public void Valid_signed_request_records_system_replay_before_invoking_repair()
    {
        var request = CreateRequest();
        WriteRequest(RemoteRepairContract.Serialize(request));
        var command = new FakeCommand();

        MakeWorker(command).TickOnce(Now);

        Assert.Equal([MaintenanceReason.RemoteRepairRequested], command.RepairCalls);
        Assert.False(File.Exists(RequestPath));
        Assert.True(File.Exists(LedgerPath));
        Assert.Contains(RemoteRepairContract.ComputeReplayId(request), File.ReadAllText(LedgerPath));
        using var telemetry = JsonDocument.Parse(File.ReadAllText(TelemetryPath));
        var repair = telemetry.RootElement.GetProperty("remoteRepair");
        Assert.Equal("repair_completed", repair.GetProperty("outcome").GetString());
        Assert.True(repair.GetProperty("repairInvoked").GetBoolean());
    }

    [Fact]
    public void Same_signed_command_cannot_invoke_repair_after_watchdog_restart()
    {
        var request = CreateRequest();
        WriteRequest(RemoteRepairContract.Serialize(request));
        var firstCommand = new FakeCommand();
        MakeWorker(firstCommand).TickOnce(Now);
        Assert.Single(firstCommand.RepairCalls);

        WriteRequest(RemoteRepairContract.Serialize(request));
        var restartedCommand = new FakeCommand();
        MakeWorker(restartedCommand).TickOnce(Now.AddSeconds(10));

        Assert.Empty(restartedCommand.RepairCalls);
        Assert.False(File.Exists(RequestPath));
        using var telemetry = JsonDocument.Parse(File.ReadAllText(TelemetryPath));
        Assert.Equal(
            "repair_request_replay",
            telemetry.RootElement.GetProperty("remoteRepair").GetProperty("outcome").GetString());
    }

    [Fact]
    public void Replacement_after_validation_cannot_change_parsed_repair_and_is_left_for_next_tick()
    {
        var original = CreateRequest();
        var replacement = CreateRequest(
            commandId: "cmd-remote-repair-2",
            reason: "operator_requested",
            nonce: "nonce-remote-repair-2");
        WriteRequest(RemoteRepairContract.Serialize(original));
        var command = new FakeCommand();

        MakeWorker(
            command,
            afterValidation: () => WriteRequest(RemoteRepairContract.Serialize(replacement)))
            .TickOnce(Now);

        Assert.Single(command.RepairCalls);
        using var telemetry = JsonDocument.Parse(File.ReadAllText(TelemetryPath));
        var repair = telemetry.RootElement.GetProperty("remoteRepair");
        Assert.Equal(original.CommandId, repair.GetProperty("commandId").GetString());
        Assert.Equal(original.Reason, repair.GetProperty("reason").GetString());
        Assert.Contains(RemoteRepairContract.ComputeReplayId(original), File.ReadAllText(LedgerPath));
        Assert.DoesNotContain(RemoteRepairContract.ComputeReplayId(replacement), File.ReadAllText(LedgerPath));
        Assert.True(RemoteRepairContract.TryDeserialize(
            File.ReadAllText(RequestPath),
            out var remaining,
            out _));
        Assert.Equal(replacement.CommandId, remaining!.CommandId);
    }

    [Fact]
    public void Corrupt_system_replay_ledger_fails_closed()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LedgerPath)!);
        File.WriteAllText(LedgerPath, "{not-valid-json");
        WriteRequest(RemoteRepairContract.Serialize(CreateRequest()));
        var command = new FakeCommand();

        MakeWorker(command).TickOnce(Now);

        Assert.Empty(command.RepairCalls);
        using var telemetry = JsonDocument.Parse(File.ReadAllText(TelemetryPath));
        Assert.Equal(
            "repair_replay_ledger_corrupt",
            telemetry.RootElement.GetProperty("remoteRepair").GetProperty("outcome").GetString());
    }

    [Fact]
    public void Malformed_or_unsigned_json_never_invokes_repair()
    {
        WriteRequest("{\"schemaVersion\":1,\"commandId\":\"cmd-unsigned\"}");
        var command = new FakeCommand();

        MakeWorker(command).TickOnce(Now);

        Assert.Empty(command.RepairCalls);
        Assert.False(File.Exists(RequestPath));
        AssertPhiFreeRejectionTelemetry();
    }

    [Fact]
    public void Stale_signed_request_never_invokes_repair()
    {
        var staleAt = Now - RemoteRepairContract.MaximumRequestAge - TimeSpan.FromSeconds(1);
        WriteRequest(RemoteRepairContract.Serialize(CreateRequest(staleAt)));
        var command = new FakeCommand();

        MakeWorker(command).TickOnce(Now);

        Assert.Empty(command.RepairCalls);
        AssertOutcome("command_timestamp_invalid_or_stale");
    }

    [Fact]
    public void Forged_signature_never_invokes_repair()
    {
        var request = CreateRequest() with
        {
            Signature = Convert.ToBase64String(new byte[64]),
        };
        WriteRequest(RemoteRepairContract.Serialize(request));
        var command = new FakeCommand();

        MakeWorker(command).TickOnce(Now);

        Assert.Empty(command.RepairCalls);
        AssertOutcome("command_signature_invalid");
    }

    [Fact]
    public void Tampered_raw_data_never_invokes_repair()
    {
        var request = CreateRequest() with
        {
            DataJson = "{\"commandId\":\"cmd-tampered\",\"reason\":\"watchdog_critical\"}",
        };
        WriteRequest(RemoteRepairContract.Serialize(request));
        var command = new FakeCommand();

        MakeWorker(command).TickOnce(Now);

        Assert.Empty(command.RepairCalls);
        AssertOutcome("command_data_hash_mismatch");
    }

    [Fact]
    public void Oversized_request_is_bounded_and_rejected()
    {
        WriteRequest(new string('x', RemoteRepairContract.MaxRequestBytes + 1));
        var command = new FakeCommand();

        MakeWorker(command).TickOnce(Now);

        Assert.Empty(command.RepairCalls);
        AssertOutcome("repair_request_unreadable");
    }

    [Fact]
    public void Reparse_request_is_rejected_without_reading_target()
    {
        if (OperatingSystem.IsWindows())
            return; // Windows production uses handle-level FILE_FLAG_OPEN_REPARSE_POINT; field validation covers it.

        Directory.CreateDirectory(Path.GetDirectoryName(RequestPath)!);
        var target = Path.Combine(_root, "untrusted-target.json");
        File.WriteAllText(target, RemoteRepairContract.Serialize(CreateRequest()));
        File.CreateSymbolicLink(RequestPath, target);
        var command = new FakeCommand();

        MakeWorker(command).TickOnce(Now);

        Assert.Empty(command.RepairCalls);
        Assert.True(File.Exists(target));
        Assert.False(File.Exists(RequestPath));
        AssertOutcome("repair_request_unreadable");
    }

    [Fact]
    public void Dangling_reparse_request_is_consumed_instead_of_silently_looping()
    {
        if (OperatingSystem.IsWindows())
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(RequestPath)!);
        File.CreateSymbolicLink(RequestPath, Path.Combine(_root, "missing-target.json"));
        var command = new FakeCommand();

        MakeWorker(command).TickOnce(Now);

        Assert.Empty(command.RepairCalls);
        Assert.Null(new FileInfo(RequestPath).LinkTarget);
        AssertOutcome("repair_request_unreadable");
    }

    private WatchdogWorker MakeWorker(
        FakeCommand command,
        Action? afterValidation = null) => new(
        NullLogger<WatchdogWorker>.Instance,
        command,
        new WatchdogOptions
        {
            WatchedServices = [],
            RepairRequestPath = RequestPath,
            RemoteRepairReplayLedgerPath = LedgerPath,
            RemoteRepairTrustedPublicKeys = new Dictionary<string, string>
            {
                [KeyId] = Convert.ToBase64String(_signingKey.ExportSubjectPublicKeyInfo()),
            },
            RemoteRepairAfterValidationForTests = afterValidation,
            ExpectedAgentId = AgentId,
            ExpectedMachineFingerprint = Fingerprint,
            TelemetryPath = TelemetryPath,
            UpdateRoot = Path.Combine(_root, "updates"),
            ActivationRequestPath = Path.Combine(_root, "updates", "absent.json"),
            ReplayLedgerPath = Path.Combine(_root, "updates", "activation-replay.json"),
            MaintenanceRoot = Path.Combine(_root, "maintenance"),
            ActiveClaimPath = Path.Combine(_root, "maintenance", "absent-claim.json"),
            ActivationCompletionPath = Path.Combine(_root, "maintenance", "absent-completion.json"),
            ReapplyHelperExeGrant = _ => true,
        });

    private RemoteRepairRequest CreateRequest(
        DateTimeOffset? timestamp = null,
        string commandId = "cmd-remote-repair-1",
        string reason = "watchdog_critical",
        string nonce = "nonce-remote-repair-1")
    {
        var commandAt = timestamp ?? Now;
        var dataJson = JsonSerializer.Serialize(new
        {
            commandId,
            reason,
            expiresAt = commandAt.AddMinutes(4).ToString("O"),
        });
        var dataHash = RemoteCommandTrust.ComputeSha256Hex(dataJson);
        var canonical = RemoteCommandTrust.BuildCommandCanonical(
            "repair_agent",
            AgentId,
            Fingerprint,
            commandAt.ToString("O"),
            nonce,
            dataHash);
        return new RemoteRepairRequest(
            RemoteRepairContract.SchemaVersion,
            "repair_agent",
            AgentId,
            Fingerprint,
            commandAt.ToString("O"),
            nonce,
            KeyId,
            Convert.ToBase64String(_signingKey.SignData(
                Encoding.UTF8.GetBytes(canonical),
                HashAlgorithmName.SHA256)),
            dataJson,
            dataHash,
            commandId,
            reason,
            commandAt.ToString("O"));
    }

    private void WriteRequest(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(RequestPath)!);
        File.WriteAllText(RequestPath, json, new UTF8Encoding(false));
    }

    private void AssertOutcome(string expected)
    {
        using var telemetry = JsonDocument.Parse(File.ReadAllText(TelemetryPath));
        Assert.Equal(
            expected,
            telemetry.RootElement.GetProperty("remoteRepair").GetProperty("outcome").GetString());
    }

    private void AssertPhiFreeRejectionTelemetry()
    {
        var json = File.ReadAllText(TelemetryPath);
        Assert.DoesNotContain("cmd-unsigned", json, StringComparison.Ordinal);
        using var telemetry = JsonDocument.Parse(json);
        var repair = telemetry.RootElement.GetProperty("remoteRepair");
        Assert.Equal("not_available", repair.GetProperty("commandId").GetString());
        Assert.Equal("validation_rejected", repair.GetProperty("reason").GetString());
        Assert.False(repair.GetProperty("repairInvoked").GetBoolean());
    }

    public void Dispose()
    {
        _signingKey.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class FakeCommand : IServiceCommand
    {
        public List<MaintenanceReason> RepairCalls { get; } = [];
        public ServiceState Query(string serviceName) => ServiceState.Running;
        public bool Start(string serviceName, TimeSpan timeout) => true;
        public bool Stop(string serviceName, TimeSpan timeout) => true;
        public bool InvokeRepair(MaintenanceReason reason, TimeSpan timeout)
        {
            RepairCalls.Add(reason);
            return true;
        }
    }
}
