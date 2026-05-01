using System.Net;
using System.Text;
using System.Text.Json;
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
    public async Task RecoverAsync_RotatesCloudKeyAndPersistsItWithoutLeakingTheRawKey()
    {
        var appSettingsPath = Path.Combine(Path.GetTempPath(), $"suavo_agent_recover_{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(appSettingsPath, """
            {
              "Agent": {
                "AgentId": "2a492d97-9b8c-4217-a5b1-142f8fa36602",
                "ApiKey": "stale-key",
                "MachineFingerprint": "fp-test"
              }
            }
            """);
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"success":true,"data":{"apiKey":"sagent_new_secret_key"}}""",
                Encoding.UTF8,
                "application/json"),
        });

        try
        {
            var client = new AgentCredentialRecoveryClient(
                new AgentOptions
                {
                    CloudUrl = "https://suavollc.com",
                    AgentId = "2a492d97-9b8c-4217-a5b1-142f8fa36602",
                    MachineFingerprint = "fp-test",
                },
                appSettingsPath,
                NullLogger<AgentCredentialRecoveryClient>.Instance,
                handler);

            var result = await client.TryRecoverAsync(CancellationToken.None);

            Assert.True(result.Success);
            Assert.Equal("key_rotated", result.Outcome);
            Assert.Equal("/api/agent/recover-key", handler.RequestPath);
            Assert.DoesNotContain("sagent_new_secret_key", result.ToString(), StringComparison.Ordinal);

            using var requestDoc = JsonDocument.Parse(handler.RequestBody!);
            Assert.Equal("2a492d97-9b8c-4217-a5b1-142f8fa36602",
                requestDoc.RootElement.GetProperty("agentId").GetString());
            Assert.Equal("fp-test", requestDoc.RootElement.GetProperty("machineFingerprint").GetString());

            using var savedDoc = JsonDocument.Parse(await File.ReadAllTextAsync(appSettingsPath));
            Assert.Equal(
                "sagent_new_secret_key",
                savedDoc.RootElement.GetProperty("Agent").GetProperty("ApiKey").GetString());
        }
        finally
        {
            try { File.Delete(appSettingsPath); } catch { }
        }
    }

    [Fact]
    public async Task RecoverAsync_RejectsNonUuidAgentIdBeforeCallingCloud()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var client = new AgentCredentialRecoveryClient(
            new AgentOptions
            {
                CloudUrl = "https://suavollc.com",
                AgentId = "agent-friendly-id",
                MachineFingerprint = "fp-test",
            },
            Path.Combine(Path.GetTempPath(), $"suavo_agent_recover_{Guid.NewGuid():N}.json"),
            NullLogger<AgentCredentialRecoveryClient>.Instance,
            handler);

        var result = await client.TryRecoverAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("agent_id_not_recoverable", result.Outcome);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task RecoverAsync_ReturnsConfigWriteFailureInsteadOfGenericRecoveryException()
    {
        var root = Path.Combine(Path.GetTempPath(), $"suavo_agent_recover_dir_{Guid.NewGuid():N}");
        var appSettingsPath = Path.Combine(root, "appsettings.json");
        Directory.CreateDirectory(appSettingsPath);
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"success":true,"data":{"apiKey":"sagent_new_secret_key"}}""",
                Encoding.UTF8,
                "application/json"),
        });

        try
        {
            var client = new AgentCredentialRecoveryClient(
                new AgentOptions
                {
                    CloudUrl = "https://suavollc.com",
                    AgentId = "2a492d97-9b8c-4217-a5b1-142f8fa36602",
                    MachineFingerprint = "fp-test",
                },
                appSettingsPath,
                NullLogger<AgentCredentialRecoveryClient>.Instance,
                handler);

            var result = await client.TryRecoverAsync(CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal("appsettings_write_failed", result.Outcome);
            Assert.Equal(1, handler.Calls);
            Assert.DoesNotContain("sagent_new_secret_key", result.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
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
}
