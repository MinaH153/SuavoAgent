using System.Net;
using System.Text;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using Xunit;

namespace SuavoAgent.Core.Tests.Cloud;

public sealed class SuavoCloudClientTests
{
    [Fact]
    public async Task PostSignedAsync_IncludesSanitizedCloudReasonOnAuthFailure()
    {
        using var client = new SuavoCloudClient(
            new AgentOptions
            {
                ApiKey = "agent-secret",
                CloudUrl = "https://suavollc.com",
            },
            new FixedHandler(
                new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent(
                        """{"success":false,"error":"Agent not found"}""",
                        Encoding.UTF8,
                        "application/json"),
                }));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.PostSignedAsync("/api/agent/heartbeat", new { ok = true }, CancellationToken.None));

        Assert.Contains("401", ex.Message);
        Assert.Contains("reason=Agent not found", ex.Message);
    }

    [Fact]
    public async Task PostSignedAsync_RejectsPhiShapedPayloadToNonPhiEndpointBeforeSending()
    {
        using var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json"),
            });
        using var client = new SuavoCloudClient(
            new AgentOptions
            {
                ApiKey = "agent-secret",
                CloudUrl = "https://suavollc.com",
            },
            handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.PostSignedAsync(
                "/api/agent/heartbeat",
                new { status = "ok", detail = "Patient: Jane Rivera DOB 04/12/1980 phone 555-123-4567" },
                CancellationToken.None));

        Assert.Contains("PHI-classified payload", ex.Message);
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public async Task PostSignedAsync_RejectsLegacyRxDeliveryQueueOnSyncByDefault()
    {
        using var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json"),
            });
        using var client = new SuavoCloudClient(
            new AgentOptions
            {
                ApiKey = "agent-secret",
                CloudUrl = "https://suavollc.com",
            },
            handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.PostSignedAsync(
                "/api/agent/sync",
                new
                {
                    snapshotType = "rx_delivery_queue",
                    data = new
                    {
                        rxDeliveryQueue = new[]
                        {
                            new
                            {
                                rxNumber = new string('a', 64),
                                patientFirstName = "Jane",
                                patientPhone = "555-123-4567",
                                deliveryAddress1 = "123 Main Street"
                            }
                        }
                    }
                },
                CancellationToken.None));

        Assert.Contains("PHI-classified payload", ex.Message);
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public async Task PostSignedAsync_AllowsHashOnlyCandidateSyncByDefault()
    {
        using var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json"),
            });
        using var client = new SuavoCloudClient(
            new AgentOptions
            {
                ApiKey = "agent-secret",
                CloudUrl = "https://suavollc.com",
            },
            handler);

        await client.PostSignedAsync(
            "/api/agent/sync",
            new
            {
                snapshotType = "rx_delivery_queue",
                data = new
                {
                    rxOrderCandidates = new[]
                    {
                        new
                        {
                            rxHash = new string('a', 64),
                            patientDelivery = new
                            {
                                phoneHash = new string('b', 64),
                                zip5 = "92101"
                            },
                            provenance = new { evidenceId = "rxh-aaaaaaaaaaaaaaaa-1770000000" }
                        }
                    }
                }
            },
            CancellationToken.None);

        Assert.Equal(1, handler.SendCount);
    }

    [Fact]
    public async Task PostSignedAsync_AllowsLegacyRxDeliveryQueueOnSyncOnlyWhenExplicitlyEnabled()
    {
        using var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json"),
            });
        using var client = new SuavoCloudClient(
            new AgentOptions
            {
                ApiKey = "agent-secret",
                CloudUrl = "https://suavollc.com",
                EnableLegacyPhiDeliveryQueueSync = true,
            },
            handler);

        await client.PostSignedAsync(
            "/api/agent/sync",
            new
            {
                snapshotType = "rx_delivery_queue",
                data = new
                {
                    rxDeliveryQueue = new[]
                    {
                        new
                        {
                            rxNumber = new string('a', 64),
                            patientFirstName = "Jane",
                            patientPhone = "555-123-4567",
                            deliveryAddress1 = "123 Main Street"
                        }
                    }
                }
            },
            CancellationToken.None);

        Assert.Equal(1, handler.SendCount);
    }

    [Fact]
    public async Task PostSignedAsync_AllowsPatientDetailsPathAsExplicitPhiChannel()
    {
        using var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json"),
            });
        using var client = new SuavoCloudClient(
            new AgentOptions
            {
                ApiKey = "agent-secret",
                CloudUrl = "https://suavollc.com",
            },
            handler);

        await client.PostSignedAsync(
            "/api/agent/patient-details",
            new
            {
                rxNumberHash = new string('a', 64),
                details = new
                {
                    firstName = "Jane",
                    lastInitial = "R",
                    phone = "555-123-4567",
                    address1 = "123 Main Street",
                    city = "San Diego",
                    state = "CA",
                    zip = "92101"
                },
                commandId = "cmd_1"
            },
            CancellationToken.None);

        Assert.Equal(1, handler.SendCount);
    }

    [Fact]
    public async Task SendPatientDetailsAsync_FailsClosedByDefault_DoesNotShipPhi()
    {
        using var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json"),
            });
        using var client = new SuavoCloudClient(
            new AgentOptions
            {
                ApiKey = "agent-secret",
                CloudUrl = "https://suavollc.com",
                // EnableAuditedPatientDetailsEgress defaults to false
            },
            handler);

        var sent = await client.SendPatientDetailsAsync(
            "RX123456",
            new SuavoAgent.Contracts.Models.PatientDetailsPayload(
                "Jane", "R", "555-123-4567", "123 Main Street", null, "San Diego", "CA", "92101"),
            "cmd_1",
            CancellationToken.None);

        // Precedence-1: no patient PHI may leave the box until the audited
        // /api/agent/patient-details route + phi_egress_audit exist (Stage A1).
        Assert.False(sent);
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public async Task SendPatientDetailsAsync_ShipsOnlyWhenAuditedEgressExplicitlyEnabled()
    {
        using var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json"),
            });
        using var client = new SuavoCloudClient(
            new AgentOptions
            {
                ApiKey = "agent-secret",
                CloudUrl = "https://suavollc.com",
                EnableAuditedPatientDetailsEgress = true,
            },
            handler);

        var sent = await client.SendPatientDetailsAsync(
            "RX123456",
            new SuavoAgent.Contracts.Models.PatientDetailsPayload(
                "Jane", "R", "555-123-4567", "123 Main Street", null, "San Diego", "CA", "92101"),
            "cmd_1",
            CancellationToken.None);

        Assert.True(sent);
        Assert.Equal(1, handler.SendCount);
    }

    [Fact]
    public async Task PostSignedAsync_ShadowMode_AllowsUnrecognizedToken_PreservingAvailability()
    {
        // Default (shadow) mode must NOT change behavior — a packed identifier that passes the
        // legacy <=96-char charset but is not a known token shape is STILL allowed, so the guard
        // never silently takes the agent offline. (It logs a would-block; we assert it sends.)
        using var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json"),
            });
        using var client = new SuavoCloudClient(
            new AgentOptions { ApiKey = "agent-secret", CloudUrl = "https://suavollc.com" }, // StrictOutboundTokenAllowlist defaults false
            handler);

        await client.PostSignedAsync(
            "/api/agent/heartbeat",
            new { status = "ok", marker = "DOE-JOHN-1990" }, // charset-clean, NOT a known token
            CancellationToken.None);

        Assert.Equal(1, handler.SendCount);
    }

    [Fact]
    public async Task PostSignedAsync_StrictMode_BlocksUnrecognizedTokenBeforeSending()
    {
        // Strict mode closes the <=96-char escape hatch: a packed identifier that matches no
        // operational token shape is BLOCKED before the POST.
        using var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json"),
            });
        using var client = new SuavoCloudClient(
            new AgentOptions { ApiKey = "agent-secret", CloudUrl = "https://suavollc.com", StrictOutboundTokenAllowlist = true },
            handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.PostSignedAsync(
                "/api/agent/heartbeat",
                new { status = "ok", marker = "DOE-JOHN-1990" },
                CancellationToken.None));

        Assert.Contains("PHI-classified payload", ex.Message);
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public async Task PostSignedAsync_StrictMode_BlocksGeographicField()
    {
        // city/state/zip5 are Safe-Harbor identifiers — strict mode blocks them on non-PHI paths.
        using var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json"),
            });
        using var client = new SuavoCloudClient(
            new AgentOptions { ApiKey = "agent-secret", CloudUrl = "https://suavollc.com", StrictOutboundTokenAllowlist = true },
            handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.PostSignedAsync(
                "/api/agent/sync",
                new { snapshotType = "x", data = new { zip5 = "92101" } },
                CancellationToken.None));

        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public async Task PostSignedAsync_StrictMode_AllowsKnownTokenShapes()
    {
        // Machine token shapes (uuid, hex hash, semver) stay safe even under strict mode and even
        // under non-operational field names.
        using var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json"),
            });
        using var client = new SuavoCloudClient(
            new AgentOptions { ApiKey = "agent-secret", CloudUrl = "https://suavollc.com", StrictOutboundTokenAllowlist = true },
            handler);

        await client.PostSignedAsync(
            "/api/agent/heartbeat",
            new
            {
                status = "ok",
                runToken = "3f2504e0-4f89-41d3-9a0c-0305e82c3301", // uuid
                build = "v3.19.4",                                  // semver
                fingerprint = new string('a', 32),                 // hex hash
            },
            CancellationToken.None);

        Assert.Equal(1, handler.SendCount);
    }

    private sealed class FixedHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public FixedHandler(HttpResponseMessage response)
        {
            _response = response;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(_response);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public RecordingHandler(HttpResponseMessage response)
        {
            _response = response;
        }

        public int SendCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            SendCount++;
            return Task.FromResult(_response);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _response.Dispose();
            base.Dispose(disposing);
        }
    }
}
