using System.Reflection;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Contracts.Security;
using SuavoAgent.Core.Behavioral;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Autonomy;
using SuavoAgent.Core.Ipc;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.Pricing;
using SuavoAgent.Core.State;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.Workers;

/// <summary>
/// Tests for HeartbeatWorker command dispatch, signed command verification integration,
/// and feedback command handling. Uses reflection to invoke ProcessSignedCommandAsync
/// since HeartbeatWorker's handlers are private.
/// </summary>
public partial class HeartbeatWorkerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _repairRequestPath;
    private readonly string _observationDirectory;
    private readonly AgentStateDb _db;
    private readonly HeartbeatWorker _worker;
    private readonly FakeIntentCursorClient _intentCursorClient = new();
    private readonly FakePricingJobExecutor _pricingJobExecutor = new();
    private readonly FakeHeartbeatIpcCommandClient _ipcCommandClient = new();
    private readonly FakeTopDispensedWorklistBuilder _worklistBuilder = new();
    private readonly AutopilotRunCoordinator _autopilotRuns = new();
    private readonly PricingTerminalAckOutbox _pricingTerminalAckOutbox;
    private readonly ECDsa _signingKey;
    private readonly ObservationActivationAuthority _observationAuthority;
    private readonly MutableObservationTimeProvider _observationClock;
    private readonly string _pubKeyDer;
    private readonly MethodInfo _processMethod;
    private readonly MethodInfo _runPricingMethod;
    private const string TestAgentId = "11111111-1111-4111-8111-111111111111";
    private const string TestFingerprint = "fp-hb-test";
    private const string TestPharmacyId = "22222222-2222-4222-8222-222222222222";
    private const string TestKeyId = "suavo-cmd-v1";

    public HeartbeatWorkerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"suavo_hb_{Guid.NewGuid():N}.db");
        _repairRequestPath = Path.Combine(Path.GetTempPath(), $"suavo_watchdog_repair_{Guid.NewGuid():N}.json");
        _observationDirectory = Path.Combine(
            Path.GetTempPath(),
            $"suavo_hb_observation_{Guid.NewGuid():N}");
        _db = new AgentStateDb(_dbPath);

        _signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _pubKeyDer = Convert.ToBase64String(_signingKey.ExportSubjectPublicKeyInfo());

        Directory.CreateDirectory(_observationDirectory);
        var observationIdentity = new ObservationActivationIdentity(
            TestAgentId,
            TestAgentId,
            TestPharmacyId,
            TestFingerprint,
            new string('a', 64),
            "pharmacy-field-rc",
            ObservationActivationIdentityStore.PolicyDigest);
        _observationClock = new MutableObservationTimeProvider(
            DateTimeOffset.UtcNow);
        var observationStatePath = Path.Combine(_observationDirectory, "current.json");
        var observationHighWaterPath = Path.Combine(_observationDirectory, "highwater.json");
        var observationControlPath = Path.Combine(
            _observationDirectory,
            ObservationControlStateStore.FileName);
        Assert.True(ObservationControlStateStore.TryInitialize(
            observationControlPath,
            observationIdentity,
            _observationClock.GetUtcNow()));
        _observationAuthority = new ObservationActivationAuthority(
            observationStatePath,
            observationHighWaterPath,
            observationIdentity,
            new Dictionary<string, string> { [TestKeyId] = _pubKeyDer },
            _observationClock,
            observationControlPath);
        Assert.True(_observationAuthority.TryInstall(
            SignedObservationState(observationIdentity)).Succeeded);

        var services = new ServiceCollection();
        services.AddSingleton(_db);
        services.AddSingleton<IIntentCursorClient>(_intentCursorClient);
        services.AddSingleton<IPricingJobExecutor>(_pricingJobExecutor);
        services.AddSingleton<IIpcCommandClient>(_ipcCommandClient);
        services.AddSingleton<ITopDispensedWorklistBuilder>(_worklistBuilder);
        services.AddSingleton(_autopilotRuns);
        services.AddSingleton(_observationAuthority);
        _pricingTerminalAckOutbox = new PricingTerminalAckOutbox(
            _db,
            (_, _, _, _, _) => Task.FromResult(true),
            NullLogger<PricingTerminalAckOutbox>.Instance,
            new Dictionary<string, string> { [TestKeyId] = _pubKeyDer });
        services.AddSingleton(_pricingTerminalAckOutbox);
        services.AddSingleton<IActivePmsAdapterRegistry>(new FakePomRegistry());
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        var sp = services.BuildServiceProvider();

        var options = Options.Create(new AgentOptions
        {
            AgentId = TestAgentId,
            MachineFingerprint = TestFingerprint,
            PharmacyId = TestPharmacyId,
            HeartbeatIntervalSeconds = 30,
            WatchdogRepairRequestPath = _repairRequestPath,
            // These worker tests exercise the SQL-path pricing dispatch via an injected fake executor.
            // The product default is now UiaFirst (stealth UI-driven, routes through Helper IPC that
            // isn't mocked here), so pin SqlFirst explicitly to keep testing the spec-routing behavior.
            PricingExecutor = PricingExecutorMode.SqlFirst,
            TestHooks = new TestHooksOptions { Enabled = true },
        });

        _worker = new HeartbeatWorker(
            NullLogger<HeartbeatWorker>.Instance, options, sp, _db);

        // Inject our test key into the _commandVerifier via reflection
        var verifierField = typeof(HeartbeatWorker)
            .GetField("_commandVerifier", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var testVerifier = new SignedCommandVerifier(
            new Dictionary<string, string> { [TestKeyId] = _pubKeyDer },
            TestAgentId, TestFingerprint);
        verifierField.SetValue(_worker, testVerifier);

        _processMethod = typeof(HeartbeatWorker)
            .GetMethod("ProcessSignedCommandAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        _runPricingMethod = typeof(HeartbeatWorker)
            .GetMethod("HandleRunPricingJobAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_repairRequestPath); } catch { }
        try { Directory.Delete(_observationDirectory, recursive: true); } catch { }
    }

    // ── Helpers ──

    private SignedCommand Sign(string command, string? dataJson = null)
    {
        var ts = DateTimeOffset.UtcNow.ToString("o");
        var nonce = Guid.NewGuid().ToString();
        var dataHash = SignedCommandVerifier.ComputeDataHash(dataJson);
        var canonical = $"{command}|{TestAgentId}|{TestFingerprint}|{ts}|{nonce}|{dataHash}";
        var sig = Convert.ToBase64String(
            _signingKey.SignData(Encoding.UTF8.GetBytes(canonical), HashAlgorithmName.SHA256));
        return new SignedCommand(command, TestAgentId, TestFingerprint, ts, nonce, TestKeyId, sig, dataHash);
    }

    private ObservationActivationState SignedObservationState(
        ObservationActivationIdentity identity)
    {
        var issuedAt = _observationClock.GetUtcNow().AddSeconds(-1);
        var data = new ObservationActivationLeaseData(
            1,
            "33333333-3333-4333-8333-333333333333",
            "44444444-4444-4444-8444-444444444444",
            new string('b', 64),
            identity.PharmacyId,
            identity.WorkstationId,
            identity.DeviceKeyId,
            identity.ReleaseCohort,
            1,
            identity.PolicyDigest,
            issuedAt,
            issuedAt,
            issuedAt.AddSeconds(120),
            "55555555-5555-4555-8555-555555555555");
        var dataJson = JsonSerializer.Serialize(data, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
        var dataHash = RemoteCommandTrust.ComputeSha256Hex(dataJson);
        var timestamp = issuedAt.ToString("O");
        var nonce = "66666666-6666-4666-8666-666666666666";
        var canonical = RemoteCommandTrust.BuildCommandCanonical(
            ObservationActivationAuthority.CommandName,
            identity.AgentId,
            identity.MachineFingerprint,
            timestamp,
            nonce,
            dataHash);
        var signature = Convert.ToBase64String(_signingKey.SignData(
            Encoding.UTF8.GetBytes(canonical),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        return new ObservationActivationState(1, new ObservationActivationSignedLease(
            ObservationActivationAuthority.CommandName,
            identity.AgentId,
            identity.MachineFingerprint,
            timestamp,
            nonce,
            TestKeyId,
            signature,
            dataHash,
            dataJson));
    }

    private sealed class MutableObservationTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        internal void Advance(TimeSpan interval) => _now += interval;
    }

    /// <summary>
    /// Builds a heartbeat response JSON containing a signedCommand envelope
    /// in the shape the worker expects: { data: { signedCommand: { ... } } }
    /// </summary>
    private JsonElement BuildResponseJson(string command, object? data = null)
    {
        var boundData = BindLiveCommandExpiry(command, data);
        var cmd = Sign(command, boundData != null ? JsonSerializer.Serialize(boundData) : null);
        return BuildResponseJson(cmd, boundData);
    }

    private static object? BindLiveCommandExpiry(string command, object? data)
    {
        if (!SignedCommandVerifier.RequiresLiveExpiry(command))
            return data;
        var payload = JsonSerializer.SerializeToNode(data ?? new { })?.AsObject()
            ?? new JsonObject();
        if (!payload.ContainsKey("expiresAt"))
            payload["expiresAt"] = DateTimeOffset.UtcNow.AddMinutes(4).ToString("o");
        return payload;
    }

    private static JsonElement BuildResponseJson(SignedCommand cmd, object? data = null)
    {
        var envelope = new Dictionary<string, object?>
        {
            ["command"] = cmd.Command,
            ["agentId"] = cmd.AgentId,
            ["machineFingerprint"] = cmd.MachineFingerprint,
            ["timestamp"] = cmd.Timestamp,
            ["nonce"] = cmd.Nonce,
            ["keyId"] = cmd.KeyId,
            ["signature"] = cmd.Signature,
        };
        if (data != null)
            envelope["data"] = data;

        var response = new Dictionary<string, object>
        {
            ["data"] = new Dictionary<string, object> { ["signedCommand"] = envelope }
        };
        return JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(response));
    }

    private async Task InvokeProcessAsync(JsonElement response)
    {
        var task = (Task)_processMethod.Invoke(_worker, new object[] { response, CancellationToken.None })!;
        await task;
    }

    private async Task InvokeRunPricingAsync(JsonElement signedCommand)
    {
        RegisterPricingCommandForDirectInvocation(
            _pricingTerminalAckOutbox,
            signedCommand);
        var task = (Task)_runPricingMethod.Invoke(_worker, new object[] { signedCommand, CancellationToken.None })!;
        await task;
    }

    private static void RegisterPricingCommandForDirectInvocation(
        PricingTerminalAckOutbox outbox,
        JsonElement signedCommand)
    {
        var data = signedCommand.GetProperty("data");
        var commandId = data.TryGetProperty("commandId", out var commandIdElement)
            ? commandIdElement.GetString()
            : null;
        if (!PricingTerminalAck.IsCanonicalCommandId(commandId))
            return;

        var dataHash = SignedCommandVerifier.ComputeDataHash(data.GetRawText());
        var expiresAt = data.TryGetProperty("expiresAt", out var expiresAtElement) &&
            expiresAtElement.ValueKind == JsonValueKind.String
                ? expiresAtElement.GetString()
                : null;
        var approvalId = data.TryGetProperty("approvalId", out var approvalIdElement) &&
            approvalIdElement.ValueKind == JsonValueKind.String
                ? approvalIdElement.GetString()
                : null;
        var grantDigest = data.TryGetProperty("grantDigest", out var grantDigestElement) &&
            grantDigestElement.ValueKind == JsonValueKind.String
                ? grantDigestElement.GetString()
                : null;
        var command = new SignedCommand(
            signedCommand.GetProperty("command").GetString() ?? string.Empty,
            signedCommand.GetProperty("agentId").GetString() ?? string.Empty,
            signedCommand.GetProperty("machineFingerprint").GetString() ?? string.Empty,
            signedCommand.GetProperty("timestamp").GetString() ?? string.Empty,
            signedCommand.GetProperty("nonce").GetString() ?? string.Empty,
            signedCommand.GetProperty("keyId").GetString() ?? string.Empty,
            signedCommand.GetProperty("signature").GetString() ?? string.Empty,
            dataHash,
            expiresAt);
        Assert.True(outbox.TryRegisterVerifiedCommand(
            command,
            commandId!,
            command.Command,
            approvalId,
            grantDigest));
    }

    private static string FrozenPom(string sessionId, string templateDigest) =>
        JsonSerializer.Serialize(new
        {
            sessionId,
            pharmacyId = TestPharmacyId,
            phase = "model",
            learnedAdapterTemplate = new
            {
                sessionId,
                templateDigest,
                sourceIdentityDigest = new string('b', 64),
                schemaContractDigest = new string('c', 64),
            },
        });

}
