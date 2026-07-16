using System.Net;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Health;
using Xunit;

namespace SuavoAgent.Core.Tests.Cloud;

public sealed class AgentCredentialRecoveryTests
{
    [Fact]
    public async Task RecoverAsync_RequiresApprovedDeviceRepairWithoutCallingPublicRecovery()
    {
        var store = new InMemoryCredentialStore();
        store.Set(CredentialKeys.AuthKey, "sagent_stale_key");
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"success":true,"data":{"apiKey":"sagent_new_secret_key"}}""",
                Encoding.UTF8,
                "application/json"),
        });

        var client = new AgentCredentialRecoveryClient(
            new AgentOptions
            {
                CloudUrl = "https://suavollc.com",
                AgentId = "2a492d97-9b8c-4217-a5b1-142f8fa36602",
                MachineFingerprint = "fp-test",
            },
            store,
            NullLogger<AgentCredentialRecoveryClient>.Instance,
            handler);

        var result = await client.TryRecoverAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("device_repair_required", result.Outcome);
        Assert.False(result.RestartRequired);
        Assert.Equal(0, handler.Calls);
        Assert.Equal("sagent_stale_key", store.Get(CredentialKeys.AuthKey));
    }

    [Fact]
    public async Task RecoverAsync_RequiresDeviceRepairEvenForLegacyAgentIdentity()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var client = new AgentCredentialRecoveryClient(
            new AgentOptions
            {
                CloudUrl = "https://suavollc.com",
                AgentId = "agent-friendly-id",
                MachineFingerprint = "fp-test",
            },
            new InMemoryCredentialStore(),
            NullLogger<AgentCredentialRecoveryClient>.Instance,
            handler);

        var result = await client.TryRecoverAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("device_repair_required", result.Outcome);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task RecoverAsync_NeverTouchesCredentialStore()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"success":true,"data":{"apiKey":"sagent_new_secret_key"}}""",
                Encoding.UTF8,
                "application/json"),
        });

        var client = new AgentCredentialRecoveryClient(
            new AgentOptions
            {
                CloudUrl = "https://suavollc.com",
                AgentId = "2a492d97-9b8c-4217-a5b1-142f8fa36602",
                MachineFingerprint = "fp-test",
            },
            new FailingCredentialStore(),
            NullLogger<AgentCredentialRecoveryClient>.Instance,
            handler);

        var result = await client.TryRecoverAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("device_repair_required", result.Outcome);
        Assert.Equal(0, handler.Calls);
        Assert.DoesNotContain("sagent_new_secret_key", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Coordinator_StopsApplicationOnceAfterRecoverableAgentNotFound()
    {
        var recovery = new FakeRecoveryClient(
            new AgentCredentialRecoveryResult(true, "key_rotated", RestartRequired: true));
        var lifetime = new FakeLifetime();
        var coordinator = new CloudAuthRecoveryCoordinator(
            recovery,
            lifetime,
            NullLogger<CloudAuthRecoveryCoordinator>.Instance);
        var ex = new HttpRequestException(
            "Cloud request /api/agent/heartbeat failed with 401 (Unauthorized); reason=Agent not found",
            null,
            HttpStatusCode.Unauthorized);

        Assert.True(await coordinator.TryRecoverAfterAuthFailureAsync(ex, CancellationToken.None));
        Assert.False(await coordinator.TryRecoverAfterAuthFailureAsync(ex, CancellationToken.None));
        Assert.Equal(1, recovery.Calls);
        Assert.Equal(1, lifetime.StopCalls);
    }

    [Fact]
    public async Task Coordinator_WritesCloudAuthHealthWhenRecoveryEndpointFails()
    {
        var root = Path.Combine(Path.GetTempPath(), $"suavo_cloud_auth_health_{Guid.NewGuid():N}");
        var healthPath = RuntimeHealthEvidence.CloudAuthHealthPath(root);
        var recovery = new FakeRecoveryClient(
            new AgentCredentialRecoveryResult(false, "http_404_Not_Found", RestartRequired: false));
        var coordinator = new CloudAuthRecoveryCoordinator(
            recovery,
            new FakeLifetime(),
            NullLogger<CloudAuthRecoveryCoordinator>.Instance,
            healthPath);
        var ex = new HttpRequestException(
            "Cloud request /api/agent/heartbeat failed with 401 (Unauthorized); reason=Agent not found",
            null,
            HttpStatusCode.Unauthorized);

        try
        {
            Assert.False(await coordinator.TryRecoverAfterAuthFailureAsync(ex, CancellationToken.None));

            var cloudAuth = RuntimeHealthEvidence.ReadCloudAuthHealth(healthPath);
            Assert.True(cloudAuth.Present);
            Assert.Equal("failed", cloudAuth.Status);
            Assert.Equal("http_401_Agent_not_found", cloudAuth.LastErrorKind);
            Assert.True(cloudAuth.RecoveryAttempted);
            Assert.Equal("http_404_Not_Found", cloudAuth.RecoveryOutcome);
            Assert.False(cloudAuth.RestartRequested);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public CapturingHandler(HttpResponseMessage response)
        {
            _response = response;
        }

        public int Calls { get; private set; }
        public string? RequestPath { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            RequestPath = request.RequestUri?.PathAndQuery;
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return _response;
        }
    }

    private sealed class FakeRecoveryClient : IAgentCredentialRecoveryClient
    {
        private readonly AgentCredentialRecoveryResult _result;

        public FakeRecoveryClient(AgentCredentialRecoveryResult result)
        {
            _result = result;
        }

        public int Calls { get; private set; }

        public Task<AgentCredentialRecoveryResult> TryRecoverAsync(CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(_result);
        }
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public int StopCalls { get; private set; }
        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopped.Token;

        public void StopApplication()
        {
            StopCalls++;
        }
    }

    private sealed class FailingCredentialStore : IEncryptedCredentialStore
    {
        public string? Get(string key) => null;
        public void Set(string key, string value) => throw new IOException("write denied");
        public void SetMany(IReadOnlyDictionary<string, string> values) => throw new IOException("write denied");
        public void Delete(string key) => throw new IOException("write denied");
        public void DeleteMany(IReadOnlyCollection<string> keys) => throw new IOException("write denied");
    }
}
