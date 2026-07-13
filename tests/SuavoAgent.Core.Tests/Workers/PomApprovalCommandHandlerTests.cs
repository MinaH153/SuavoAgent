using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.State;
using SuavoAgent.Core.Workers;
using SuavoAgent.Contracts.Security;
using Xunit;

namespace SuavoAgent.Core.Tests.Workers;

public sealed class PomApprovalCommandHandlerTests : IDisposable
{
    private const string AgentId = "66666666-6666-4666-8666-666666666666";
    private const string Fingerprint = "pom-handler-fingerprint";
    private const string PharmacyId = "11111111-1111-4111-8111-111111111111";
    private const string SessionId = "learn-22222222-2222-4222-8222-222222222222-20260710120000";
    private const string CommandId = "33333333-3333-4333-8333-333333333333";
    private const string PomId = "44444444-4444-4444-8444-444444444444";
    private const string ApprovedBy = "55555555-5555-4555-8555-555555555555";
    private const string KeyId = "pom-handler-test-key";
    private static readonly string TemplateDigest = new('a', 64);

    private readonly AgentStateDb _db = new(":memory:");
    private readonly ECDsa _signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly CapturingHandler _handler;
    private readonly FakeRegistry _registry = new();
    private readonly SuavoCloudClient _cloudClient;
    private readonly ServiceProvider _services;
    private readonly HeartbeatWorker _worker;
    private readonly MethodInfo _process;
    private readonly string _modelDigest;

    public PomApprovalCommandHandlerTests()
    {
        _db.CreateLearningSession(SessionId, PharmacyId);
        _db.UpdateLearningPhase(SessionId, "pattern");
        _db.UpdateLearningPhase(SessionId, "model");
        var pomJson = JsonSerializer.Serialize(new
        {
            sessionId = SessionId,
            pharmacyId = PharmacyId,
            phase = "model",
            learnedAdapterTemplate = new
            {
                sessionId = SessionId,
                templateDigest = TemplateDigest,
                sourceIdentityDigest = new string('b', 64),
                schemaContractDigest = new string('c', 64),
            },
        });
        _db.StorePomSnapshot(SessionId, pomJson);
        _modelDigest = PomExporter.ComputeDigest(PharmacyId, SessionId, pomJson);

        _handler = new CapturingHandler(_db, CommandId);
        var options = new AgentOptions
        {
            ApiKey = "pom-handler-api-key",
            CloudUrl = "https://suavollc.com",
            AgentId = AgentId,
            MachineFingerprint = Fingerprint,
            PharmacyId = PharmacyId,
        };
        _cloudClient = new SuavoCloudClient(options, _handler);

        var services = new ServiceCollection();
        services.AddSingleton(_db);
        services.AddSingleton(_cloudClient);
        var deviceKeys = new InMemoryDeviceAttestationKeyProvider();
        using (var pending = deviceKeys.OpenOrCreate(Fingerprint))
            deviceKeys.CommitPending(Fingerprint, pending.Enrollment.KeyId);
        services.AddSingleton<IDeviceAuthoritySigner>(
            new DeviceAuthoritySigner(options, deviceKeys));
        services.AddSingleton<IActivePmsAdapterRegistry>(_registry);
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        _services = services.BuildServiceProvider();

        _worker = new HeartbeatWorker(
            NullLogger<HeartbeatWorker>.Instance,
            Options.Create(options),
            _services,
            _db);
        var verifierField = typeof(HeartbeatWorker)
            .GetField("_commandVerifier", BindingFlags.NonPublic | BindingFlags.Instance)!;
        verifierField.SetValue(
            _worker,
            new SignedCommandVerifier(
                new Dictionary<string, string>
                {
                    [KeyId] = Convert.ToBase64String(_signingKey.ExportSubjectPublicKeyInfo()),
                },
                AgentId,
                Fingerprint));
        _process = typeof(HeartbeatWorker)
            .GetMethod("ProcessSignedCommandAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
    }

    public void Dispose()
    {
        _cloudClient.Dispose();
        _services.Dispose();
        _signingKey.Dispose();
        _db.Dispose();
    }

    [Fact]
    public async Task SuccessAck_IsPhiFreeAndSentOnlyAfterTerminalLedgerCommit()
    {
        await InvokeAsync(BuildResponse(CommandData()));

        var request = Assert.Single(_handler.Requests);
        Assert.EndsWith("/api/agent/pom/activation-receipt", request.Path, StringComparison.Ordinal);
        Assert.Equal("pom_approval_activated", request.LedgerCodeAtSend);
        using var body = JsonDocument.Parse(request.Body);
        var receipt = body.RootElement.GetProperty("receipt");
        Assert.Equal(1, receipt.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("pom_approval_activated", receipt.GetProperty("resultCode").GetString());
        Assert.Equal(64, body.RootElement.GetProperty("keyId").GetString()!.Length);
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("signature").GetString()));
    }

    [Fact]
    public async Task ExactRedelivery_AcksOriginalSuccessWithoutReactivating()
    {
        var data = CommandData();
        await InvokeAsync(BuildResponse(data));
        await InvokeAsync(BuildResponse(data)); // fresh signed nonce, stable command id/data

        Assert.Equal(2, _handler.Requests.Count);
        Assert.Equal(1, _registry.ActivationCount);
        Assert.Equal(_handler.Requests[0].Body, _handler.Requests[1].Body);
    }

    [Fact]
    public async Task ReusedCommandIdWithDifferentPayload_FailsAndPreservesOriginalSuccess()
    {
        await InvokeAsync(BuildResponse(CommandData()));
        await InvokeAsync(BuildResponse(CommandData(templateDigest: new string('d', 64))));

        Assert.Single(_handler.Requests);
        Assert.Equal("pom_approval_activated", _db.GetPomApprovalLedger(CommandId)!.ResultCode);
        Assert.Equal(1, _registry.ActivationCount);
    }

    [Fact]
    public async Task RegistryRejection_CommitsExactFailureBeforeFailedAck()
    {
        _registry.Result = new(
            AdapterActivationOutcome.Rejected,
            "adapter_template_digest_mismatch");

        await InvokeAsync(BuildResponse(CommandData()));

        var request = Assert.Single(_handler.Requests);
        Assert.Equal("pom_approval_adapter_template_digest_mismatch", request.LedgerCodeAtSend);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal(
            "pom_approval_adapter_template_digest_mismatch",
            body.RootElement.GetProperty("receipt").GetProperty("resultCode").GetString());
    }

    [Fact]
    public async Task ExpiredCommand_IsDurablyRejectedWithoutActivationOrCloudReceipt()
    {
        await InvokeAsync(BuildResponse(CommandData(
            expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1))));

        Assert.Empty(_handler.Requests);
        Assert.Equal(0, _registry.ActivationCount);
        Assert.Equal(
            "pom_approval_command_expired",
            _db.GetPomApprovalLedger(CommandId)!.ResultCode);
    }

    private object CommandData(
        string? templateDigest = null,
        DateTimeOffset? expiresAt = null) => new
    {
        schemaVersion = 1,
        pomId = PomId,
        sessionId = SessionId,
        approvedModelDigest = _modelDigest,
        approvedTemplateDigest = templateDigest ?? TemplateDigest,
        approvedBy = ApprovedBy,
        commandId = CommandId,
        expiresAt = (expiresAt ?? DateTimeOffset.UtcNow.AddMinutes(10))
            .ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
    };

    private JsonElement BuildResponse(object data)
    {
        var dataJson = JsonSerializer.Serialize(data);
        var timestamp = DateTimeOffset.UtcNow.ToString("o");
        var nonce = Guid.NewGuid().ToString("D");
        var dataHash = SignedCommandVerifier.ComputeDataHash(dataJson);
        var canonical = $"approve_pom|{AgentId}|{Fingerprint}|{timestamp}|{nonce}|{dataHash}";
        var signature = Convert.ToBase64String(
            _signingKey.SignData(Encoding.UTF8.GetBytes(canonical), HashAlgorithmName.SHA256));
        return JsonSerializer.SerializeToElement(new
        {
            data = new
            {
                signedCommand = new
                {
                    command = "approve_pom",
                    agentId = AgentId,
                    machineFingerprint = Fingerprint,
                    timestamp,
                    nonce,
                    keyId = KeyId,
                    signature,
                    data,
                },
            },
        });
    }

    private async Task InvokeAsync(JsonElement response)
    {
        var task = (Task)_process.Invoke(
            _worker,
            new object[] { response, CancellationToken.None })!;
        await task;
    }

    private sealed class FakeRegistry : IActivePmsAdapterRegistry
    {
        public int ActivationCount { get; private set; }
        public AdapterActivationResult Result { get; set; } = new(
            AdapterActivationOutcome.Activated,
            "approved_exact_binding");

        public AdapterActivationResult ActivateApproved(string sessionId)
        {
            ActivationCount++;
            return Result;
        }

        public ActivePmsAdapterLease? TryAcquire(DateTimeOffset now) => null;
        public void ReportHealthy(ActivePmsAdapterBinding binding, DateTimeOffset now) { }
        public void ReportUnhealthy(
            ActivePmsAdapterBinding binding,
            DateTimeOffset now,
            string errorCategory) { }
        public ActivePmsAdapterStatus Snapshot(DateTimeOffset now) =>
            new(false, null, null, 0, null, null);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly AgentStateDb _db;
        private readonly string _commandId;

        public CapturingHandler(AgentStateDb db, string commandId)
        {
            _db = db;
            _commandId = commandId;
        }

        public List<CapturedRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new(
                request.RequestUri?.AbsolutePath ?? "",
                body,
                _db.GetPomApprovalLedger(_commandId)?.ResultCode));
            var code = _db.GetPomApprovalLedger(_commandId)?.ResultCode;
            var succeeded = code is "pom_approval_activated" or "pom_approval_already_active";
            var responseBody = JsonSerializer.Serialize(new
            {
                success = true,
                data = new
                {
                    commandId = _commandId,
                    status = succeeded ? "executed" : "failed",
                    sourceBindingId = succeeded
                        ? "77777777-7777-4777-8777-777777777777"
                        : null,
                    idempotent = Requests.Count > 1,
                },
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    responseBody,
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }

    private sealed record CapturedRequest(
        string Path,
        string Body,
        string? LedgerCodeAtSend);
}
