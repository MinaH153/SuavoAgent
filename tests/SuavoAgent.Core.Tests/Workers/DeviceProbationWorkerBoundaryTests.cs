using System.Net;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SuavoAgent.Contracts.Security;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Health;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.Workers;

public sealed class DeviceProbationWorkerBoundaryTests : IDisposable
{
    private readonly RecordingSigner _signer = new();

    [Fact]
    public async Task PreCancelledExecution_StillBuildsExactProvisioningProofThenExits()
    {
        using var cloud = Cloud();
        var worker = Worker(CompleteOptions(), cloud);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await InvokeExecuteAsync(worker, cancellation.Token);

        var proof = Assert.Single(_signer.ProvisioningProofs);
        Assert.Equal("device-code-1", proof.DeviceCode);
        Assert.Equal("provisioning-1", proof.ProvisioningId);
        Assert.Equal("agent-1", proof.AgentId);
        Assert.Equal("pharmacy-1", proof.PharmacyId);
        Assert.Equal("fingerprint-1", proof.Fingerprint);
        Assert.Equal("device-key-1", proof.KeyId);
        Assert.Equal("challenge-1", proof.Challenge);
    }

    [Theory]
    [InlineData("device_code", "Pending device code is unavailable.")]
    [InlineData("provisioning_id", "Pending provisioning identity is unavailable.")]
    [InlineData("agent_id", "Pending agent identity is unavailable.")]
    [InlineData("pharmacy_id", "Pending pharmacy identity is unavailable.")]
    [InlineData("fingerprint", "Pending device fingerprint is unavailable.")]
    [InlineData("key_id", "Pending device key id is unavailable.")]
    [InlineData("challenge", "Pending device challenge is unavailable.")]
    public async Task MissingProvisioningField_FailsBeforeCloudOrRuntimeInspection(
        string field,
        string expectedMessage)
    {
        var options = CompleteOptions();
        switch (field)
        {
            case "device_code": options.InstallDeviceCode = null; break;
            case "provisioning_id": options.InstallProvisioningId = null; break;
            case "agent_id": options.AgentId = null; break;
            case "pharmacy_id": options.PharmacyId = null; break;
            case "fingerprint": options.MachineFingerprint = null; break;
            case "key_id": options.DeviceAttestationKeyId = null; break;
            case "challenge": options.InstallDeviceChallenge = null; break;
        }
        using var cloud = Cloud();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => InvokeExecuteAsync(Worker(options, cloud), CancellationToken.None));

        Assert.Equal(expectedMessage, error.Message);
        Assert.Empty(_signer.ProvisioningProofs);
    }

    [Fact]
    public async Task NonOperationalRuntime_RetriesUntilCancellationWithoutPostingHealth()
    {
        using var cloud = Cloud();
        var worker = Worker(CompleteOptions(), cloud);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await InvokeExecuteAsync(worker, cancellation.Token);

        Assert.Single(_signer.ProvisioningProofs);
        Assert.Empty(_signer.ProbationHealth);
    }

    [Fact]
    public async Task OutagePastFreshnessWindow_ReplaysExactThenRefreshesOnlyAfterDefinitiveStale()
    {
        var clock = new ManualClock(
            DateTimeOffset.Parse("2026-07-13T08:00:00Z"));
        using var cancellation = new CancellationTokenSource();
        using var handler = new SequenceHandler(
            cancellation,
            attempt =>
            {
                if (attempt == 1)
                    clock.Advance(TimeSpan.FromMinutes(3));
            },
            SequenceStep.Unavailable,
            SequenceStep.ObservationStale,
            SequenceStep.Cancel);
        using var cloud = new DeviceProbationCloudClient(CompleteOptions(), handler);
        var canary = new GreenCanary();
        var worker = Worker(
            CompleteOptions(),
            cloud,
            canary,
            clock.UtcNow,
            TimeSpan.FromMilliseconds(1));

        await InvokeExecuteAsync(worker, cancellation.Token);

        Assert.Equal(3, handler.Bodies.Count);
        Assert.Equal(handler.Bodies[0], handler.Bodies[1]);
        Assert.NotEqual(handler.Bodies[1], handler.Bodies[2]);
        Assert.Equal(2, canary.ProbeCount);
        Assert.Equal(2, _signer.ProbationHealth.Count);
        Assert.Equal(
            "2026-07-13T08:00:00.0000000Z",
            _signer.ProbationHealth[0].ObservedAtUtc);
        Assert.Equal(
            "2026-07-13T08:03:00.0000000Z",
            _signer.ProbationHealth[1].ObservedAtUtc);
    }

    [Fact]
    public async Task ResponseLossPastFreshnessWindow_RetriesExactConsumedProofWithoutResigning()
    {
        var clock = new ManualClock(
            DateTimeOffset.Parse("2026-07-13T08:00:00Z"));
        using var cancellation = new CancellationTokenSource();
        using var handler = new SequenceHandler(
            cancellation,
            attempt =>
            {
                if (attempt == 1)
                    clock.Advance(TimeSpan.FromMinutes(3));
            },
            SequenceStep.ResponseLost,
            SequenceStep.Cancel);
        using var cloud = new DeviceProbationCloudClient(CompleteOptions(), handler);
        var canary = new GreenCanary();
        var worker = Worker(
            CompleteOptions(),
            cloud,
            canary,
            clock.UtcNow,
            TimeSpan.FromMilliseconds(1));

        await InvokeExecuteAsync(worker, cancellation.Token);

        Assert.Equal(2, handler.Bodies.Count);
        Assert.Equal(handler.Bodies[0], handler.Bodies[1]);
        Assert.Equal(1, canary.ProbeCount);
        var health = Assert.Single(_signer.ProbationHealth);
        Assert.Equal("2026-07-13T08:00:00.0000000Z", health.ObservedAtUtc);
        Assert.Equal(
            DateTimeOffset.Parse("2026-07-13T08:03:00Z"),
            clock.UtcNow());
    }

    public void Dispose()
    {
        _signer.Dispose();
    }

    private DeviceProbationWorker Worker(
        AgentOptions options,
        DeviceProbationCloudClient cloud,
        IPioneerRxProbationSqlCanary? canary = null,
        Func<DateTimeOffset>? utcNow = null,
        TimeSpan? retryDelay = null) => new(
            NullLogger<DeviceProbationWorker>.Instance,
            Options.Create(options),
            cloud,
            _signer,
            canary ?? new UnavailableCanary(),
            utcNow,
            retryDelay);

    private sealed class UnavailableCanary : IPioneerRxProbationSqlCanary
    {
        public Task<ProbationSqlCanaryResult> ProbeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ProbationSqlCanaryResult(
                SqlConnected: false,
                SchemaCanaryGreen: false,
                Code: "probation_sql_unavailable"));
    }

    private sealed class GreenCanary : IPioneerRxProbationSqlCanary
    {
        internal int ProbeCount { get; private set; }

        public Task<ProbationSqlCanaryResult> ProbeAsync(CancellationToken cancellationToken)
        {
            ProbeCount += 1;
            return Task.FromResult(new ProbationSqlCanaryResult(
                SqlConnected: true,
                SchemaCanaryGreen: true,
                Code: "pms_schema_canary"));
        }
    }

    private static DeviceProbationCloudClient Cloud() => new(
        CompleteOptions(),
        new NeverCalledHandler());

    private static AgentOptions CompleteOptions() => new()
    {
        ApiKey = "probation-test-key",
        CloudUrl = "https://suavollc.com",
        InstallDeviceCode = "device-code-1",
        InstallProvisioningId = "provisioning-1",
        AgentId = "agent-1",
        PharmacyId = "pharmacy-1",
        MachineFingerprint = "fingerprint-1",
        DeviceAttestationKeyId = "device-key-1",
        InstallDeviceChallenge = "challenge-1",
        Version = "4.0.0",
        SqlServer = "sql.example.invalid",
        SqlServerCertificateSha256 = new string('c', 64),
    };

    private static async Task InvokeExecuteAsync(
        DeviceProbationWorker worker,
        CancellationToken cancellationToken)
    {
        var method = typeof(DeviceProbationWorker).GetMethod(
            "ExecuteAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ExecuteAsync was not found.");
        var task = Assert.IsAssignableFrom<Task>(method.Invoke(worker, [cancellationToken]));
        await task;
    }

    private sealed class NeverCalledHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }

    private enum SequenceStep
    {
        Unavailable,
        ObservationStale,
        ResponseLost,
        Cancel,
    }

    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly CancellationTokenSource _cancellation;
        private readonly Action<int> _afterAttempt;
        private readonly IReadOnlyList<SequenceStep> _steps;
        private int _attempt;

        internal SequenceHandler(
            CancellationTokenSource cancellation,
            Action<int> afterAttempt,
            params SequenceStep[] steps)
        {
            _cancellation = cancellation;
            _afterAttempt = afterAttempt;
            _steps = steps;
        }

        internal List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Bodies.Add(body);
            var attempt = Interlocked.Increment(ref _attempt);
            _afterAttempt(attempt);
            return _steps[attempt - 1] switch
            {
                SequenceStep.Unavailable => JsonResponse(
                    HttpStatusCode.ServiceUnavailable,
                    "SERVICE_UNAVAILABLE"),
                SequenceStep.ObservationStale => JsonResponse(
                    HttpStatusCode.UnprocessableEntity,
                    "PROBATION_HEALTH_OBSERVATION_STALE"),
                SequenceStep.ResponseLost =>
                    throw new HttpRequestException("Response lost after submit."),
                SequenceStep.Cancel => Cancel(),
                _ => throw new InvalidOperationException("Unknown test step."),
            };
        }

        private HttpResponseMessage Cancel()
        {
            _cancellation.Cancel();
            throw new OperationCanceledException(_cancellation.Token);
        }

        private static HttpResponseMessage JsonResponse(
            HttpStatusCode status,
            string code) => new(status)
            {
                Content = new StringContent(
                    $$"""{"success":false,"code":"{{code}}"}""",
                    Encoding.UTF8,
                    "application/json"),
            };
    }

    private sealed class ManualClock(DateTimeOffset initial)
    {
        private DateTimeOffset _now = initial;

        internal DateTimeOffset UtcNow() => _now;

        internal void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }

    private sealed class RecordingSigner : IDeviceAuthoritySigner
    {
        internal List<DeviceProvisioningProofPayload> ProvisioningProofs { get; } = [];
        internal List<DeviceProbationHealthFields> ProbationHealth { get; } = [];
        public string KeyId => "device-key-1";

        public SignedDeviceProvisioningProof SignProvisioningProof(
            DeviceProvisioningProofPayload proof)
        {
            ProvisioningProofs.Add(proof);
            return new(
                proof.DeviceCode, proof.ProvisioningId, proof.AgentId, proof.PharmacyId,
                proof.Fingerprint, proof.KeyId, proof.Challenge,
                proof.SqlServerCertificateSha256, "signature",
                new string('a', 64));
        }

        public SignedDeviceProbationHealth SignProbationHealth(
            DeviceProbationHealthFields health)
        {
            ProbationHealth.Add(health);
            return new(health, "signature", new string('b', 64));
        }

        public SignedDeviceReceipt<PomActivationDeviceReceipt> Sign(
            PomActivationDeviceReceipt receipt) => throw new NotSupportedException();

        public SignedDeviceReceipt<RxSourceDeviceReceipt> Sign(
            RxSourceDeviceReceipt receipt) => throw new NotSupportedException();

        public SignedDeviceReceipt<SeedApplicationDeviceReceipt> Sign(
            SeedApplicationDeviceReceipt receipt) => throw new NotSupportedException();

        public SignedDeviceReceipt<AutonomyEvidenceDeviceReceipt> Sign(
            AutonomyEvidenceDeviceReceipt receipt) => throw new NotSupportedException();

        public void Dispose() { }
    }
}
