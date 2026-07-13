using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Contracts.Models;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Cloud;

public sealed partial class SuavoCloudClientTests
{
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
        public string? LastBody { get; private set; }
        public HttpMethod? LastMethod { get; private set; }
        public string? LastPath { get; private set; }
        public string? LastApiKey { get; private set; }
        public string? LastAuthVersion { get; private set; }
        public string? LastTimestamp { get; private set; }
        public string? LastNonce { get; private set; }
        public string? LastContentSha256 { get; private set; }
        public string? LastSignature { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            SendCount++;
            LastMethod = request.Method;
            LastPath = request.RequestUri?.PathAndQuery;
            LastApiKey = request.Headers.TryGetValues("x-agent-api-key", out var apiKeys)
                ? apiKeys.Single()
                : null;
            LastAuthVersion = request.Headers.TryGetValues("x-agent-auth-version", out var versions)
                ? versions.Single()
                : null;
            LastTimestamp = request.Headers.TryGetValues("x-agent-timestamp", out var timestamps)
                ? timestamps.Single()
                : null;
            LastNonce = request.Headers.TryGetValues("x-agent-nonce", out var nonces)
                ? nonces.Single()
                : null;
            LastContentSha256 = request.Headers.TryGetValues("x-agent-content-sha256", out var digests)
                ? digests.Single()
                : null;
            LastSignature = request.Headers.TryGetValues("x-agent-signature", out var signatures)
                ? signatures.Single()
                : null;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return _response;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _response.Dispose();
            base.Dispose(disposing);
        }
    }

    private static ApprovedPatientFetchCommand PatientCommand()
    {
        const string rawRx = "RX123456";
        var hash = PhiScrubber.HmacHash(rawRx, "test-hmac-key");
        return new ApprovedPatientFetchCommand(
            "00000000-0000-4000-8000-000000000002",
            hash,
            $"rxh-{hash[..16]}-1770000000",
            "00000000-0000-4000-8000-000000000001",
            "00000000-0000-4000-8000-000000000003");
    }

    private static SignedDeviceReceipt<RxSourceDeviceReceipt> SourceReceipt() => new(
        new RxSourceDeviceReceipt(
            1,
            "11111111-1111-4111-8111-111111111111",
            "22222222-2222-4222-8222-222222222222",
            "machine.fp",
            new string('a', 64),
            "learned",
            "33333333-3333-4333-8333-333333333333",
            "learned-approved",
            $"learned.template.{new string('b', 64)}",
            "44444444-4444-4444-8444-444444444444",
            "session-1",
            new string('c', 64),
            new string('b', 64),
            9,
            "2026-07-10T20:00:00.0000000Z"),
        new string('d', 64),
        new string('e', 86),
        new string('f', 64));

    private static string CallbackReceiptJson(
        ApprovedPatientFetchCommand command,
        bool idempotent,
        DateTimeOffset? expiresAt = null) =>
        JsonSerializer.Serialize(new
        {
            success = true,
            data = new
            {
                schemaVersion = 1,
                commandId = command.CommandId,
                candidateId = command.CandidateId,
                pharmacyId = command.PharmacyId,
                stagingId = "00000000-0000-4000-8000-000000000004",
                transitionId = "00000000-0000-4000-8000-000000000005",
                status = "patient_details_received",
                reviewState = "ready_for_review",
                expiresAt = (expiresAt ?? DateTimeOffset.UtcNow.AddMinutes(30)).ToString("O"),
                idempotent,
            },
        });

    private static AgentDeliveryWritebackCommand DeliveryCommand() => new(
        2,
        "00000000-0000-4000-8000-000000000010",
        "00000000-0000-4000-8000-000000000011",
        new string('a', 64),
        "rxh-aaaaaaaaaaaaaaaa-1770000000",
        "00000000-0000-4000-8000-000000000012",
        "00000000-0000-4000-8000-000000000013",
        "00000000-0000-4000-8000-000000000014",
        "00000000-0000-4000-8000-000000000016",
        "00000000-0000-4000-8000-000000000017",
        new string('b', 64),
        "complete",
        "2026-07-10T12:15:00.000Z",
        "00000000-0000-4000-8000-000000000015");

    private static string DeliveryReceiptJson(
        AgentDeliveryWritebackCommand command,
        DeliveryWritebackResultCode result,
        bool idempotent) =>
        JsonSerializer.Serialize(new
        {
            success = true,
            data = new
            {
                schemaVersion = 2,
                writebackId = command.WritebackId,
                commandId = command.CommandId,
                pharmacyId = command.PharmacyId,
                orderId = command.OrderId,
                candidateId = command.CandidateId,
                pmsReferenceId = command.PmsReferenceId,
                proofRecordId = command.ProofRecordId,
                proofDigest = command.ProofDigest,
                transition = command.Transition,
                status = result switch
                {
                    DeliveryWritebackResultCode.Success or DeliveryWritebackResultCode.AlreadyAtTarget => "succeeded",
                    _ => "needs_attention",
                },
                resultCode = result.ToWireValue(),
                completedAt = "2026-07-10T12:16:00.000Z",
                idempotent,
            },
        });
}
