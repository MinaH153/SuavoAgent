using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SuavoAgent.Core.Autonomy;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Pricing;
using SuavoAgent.Core.State;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.Workers;

public sealed class HeartbeatWorkerLoopTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(),
        "suavo-heartbeat-loop-" + Guid.NewGuid().ToString("N") + ".db");

    [Fact]
    public async Task OneSuccessfulTickBuildsPhiFreeOperationalPayloadAndProcessesCloudResponse()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $"{{\"success\":true,\"data\":{{\"updateChannel\":\"coverage\",\"serverTime\":\"{DateTimeOffset.UtcNow:O}\"}}}}",
                Encoding.UTF8,
                "application/json"),
        };
        using var handler = new CancellingRecordingHandler(response, cancellation, 350);

        await RunOneTickAsync(handler, cancellation.Token);

        Assert.Equal(1, handler.SendCount);
        Assert.Equal("/api/agent/heartbeat", handler.LastPath);
        Assert.NotNull(handler.LastBody);
        Assert.Contains("\"status\":\"online\"", handler.LastBody);
        Assert.Contains("\"machineFingerprint\":\"machine-loop\"", handler.LastBody);
        Assert.DoesNotContain("rxNumber", handler.LastBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("patientName", handler.LastBody, StringComparison.OrdinalIgnoreCase);
        using var persisted = new AgentStateDb(_path);
        Assert.True(persisted.TryAdmitPricingCloudAuthority(
            DateTimeOffset.UtcNow,
            out var leaseCode));
        Assert.Equal("pricing_cloud_authority_lease_active", leaseCode);
    }

    [Fact]
    public async Task CloudFailureIsContainedUntilCancellationWithoutSilentSuccess()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent(
                "{\"success\":false,\"error\":\"temporarily unavailable\"}",
                Encoding.UTF8,
                "application/json"),
        };
        using var handler = new CancellingRecordingHandler(response, cancellation, 250);

        await RunOneTickAsync(handler, cancellation.Token);

        Assert.Equal(1, handler.SendCount);
        Assert.Equal("/api/agent/heartbeat", handler.LastPath);
    }

    [Fact]
    public async Task ExactInactiveBindingResponsePermanentlyRevokesLocalPricingAuthority()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var response = new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent(
                "{\"success\":false,\"error\":\"Agent binding inactive\"}",
                Encoding.UTF8,
                "application/json"),
        };
        using var handler = new CancellingRecordingHandler(response, cancellation, 250);

        await RunOneTickAsync(handler, cancellation.Token);

        using var persisted = new AgentStateDb(_path);
        Assert.False(persisted.TryAdmitPricingCloudAuthority(
            DateTimeOffset.UtcNow,
            out var code));
        Assert.Equal("pricing_cloud_authority_revoked", code);
    }

    [Fact]
    public async Task PendingPricingProposal_IsSignedAndRetriedUntilReceipt()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        using var handler = new MultiTickRecordingHandler(cancellation, 2);
        SuavoAgent.Contracts.Pricing.PricingApprovalProposal? proposal = null;

        await RunOneTickAsync(
            handler,
            cancellation.Token,
            db => proposal = db.StagePricingApprovalProposal(
                "pharmacy-loop",
                "agent-loop",
                "machine-loop",
                PricingObservationPolicy.CreateUia("uia"),
                DateTimeOffset.UtcNow,
                out _));

        Assert.NotNull(proposal);
        Assert.Equal(2, handler.SendCount);
        Assert.True(handler.AllRequestsSigned);
        foreach (var body in handler.Bodies)
        {
            using var document = System.Text.Json.JsonDocument.Parse(body);
            var proposals = document.RootElement.GetProperty(
                "pricingApprovalProposals");
            var emitted = Assert.Single(proposals.EnumerateArray().ToArray());
            Assert.Equal(
                proposal!.ProposalId,
                emitted.GetProperty("proposalId").GetString());
            Assert.Equal(
                proposal.ProposalDigest,
                emitted.GetProperty("proposalDigest").GetString());
            Assert.False(emitted.TryGetProperty("patientName", out _));
            Assert.False(emitted.TryGetProperty("rxNumber", out _));
        }
    }

    private async Task RunOneTickAsync(
        HttpMessageHandler handler,
        CancellationToken token,
        Action<AgentStateDb>? arrange = null)
    {
        var options = new AgentOptions
        {
            AgentId = "agent-loop",
            MachineFingerprint = "machine-loop",
            PharmacyId = "pharmacy-loop",
            ApiKey = "test-loop-secret",
            CloudUrl = "https://suavollc.com",
            HeartbeatIntervalSeconds = 1,
            HeartbeatJitterSeconds = 0,
            Version = "coverage",
        };
        using var db = new AgentStateDb(_path);
        arrange?.Invoke(db);
        using var cloud = new SuavoCloudClient(options, handler);
        using var services = new ServiceCollection()
            .AddSingleton(db)
            .AddSingleton(cloud)
            .AddSingleton(new AutopilotRunCoordinator())
            .BuildServiceProvider();
        var worker = new HeartbeatWorker(
            NullLogger<HeartbeatWorker>.Instance,
            Options.Create(options),
            services,
            db);

        var method = typeof(HeartbeatWorker).GetMethod(
            "RunAsync",
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task>(method!.Invoke(worker, [token]));
        try
        {
            await task;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Cancellation is the expected BackgroundService shutdown signal.
        }
    }

    private sealed class MultiTickRecordingHandler(
        CancellationTokenSource cancellation,
        int cancelAfter) : HttpMessageHandler
    {
        internal int SendCount { get; private set; }
        internal bool AllRequestsSigned { get; private set; } = true;
        internal List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            SendCount++;
            Bodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));
            AllRequestsSigned &= new[]
            {
                "x-agent-auth-version",
                "x-agent-api-key",
                "x-agent-timestamp",
                "x-agent-nonce",
                "x-agent-content-sha256",
                "x-agent-signature",
            }.All(name => request.Headers.Contains(name));
            if (SendCount >= cancelAfter)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(100, CancellationToken.None);
                    cancellation.Cancel();
                });
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"{{\"success\":true,\"data\":{{\"serverTime\":\"{DateTimeOffset.UtcNow:O}\"}}}}",
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }

    private sealed class CancellingRecordingHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;
        private readonly CancellationTokenSource _cancellation;
        private readonly int _delayMilliseconds;

        internal CancellingRecordingHandler(
            HttpResponseMessage response,
            CancellationTokenSource cancellation,
            int delayMilliseconds)
        {
            _response = response;
            _cancellation = cancellation;
            _delayMilliseconds = delayMilliseconds;
        }

        internal int SendCount { get; private set; }
        internal string? LastPath { get; private set; }
        internal string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            SendCount++;
            LastPath = request.RequestUri?.PathAndQuery;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            _ = Task.Run(async () =>
            {
                await Task.Delay(_delayMilliseconds, CancellationToken.None);
                _cancellation.Cancel();
            });
            return _response;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _response.Dispose();
            base.Dispose(disposing);
        }
    }

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
    }
}
