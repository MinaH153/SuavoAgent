using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Contracts.Security;
using SuavoAgent.Setup.Security;
using Xunit;

namespace SuavoAgent.Setup.Tests.Security;

public sealed class PioneerRxApprovalProposalTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);
    private const string AgentId = "11111111-1111-1111-1111-111111111111";
    private const string PharmacyId = "22222222-2222-2222-2222-222222222222";
    private const string MachineId = "33333333-3333-3333-3333-333333333333";
    private const string ReceiptId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    private const string CatalogId = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
    private const string ProposalId = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";
    private readonly ECDsa _cloud = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly FakeMaintenanceKeys _maintenance = new();

    [Fact]
    public async Task Catalog_discovery_uses_dual_auth_and_proposal_preserves_reserved_challenge()
    {
        Dictionary<string, string>? capturedHeaders = null;
        var catalog = Catalog();
        var challenge = Challenge();
        using var handler = new RecordingHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(PioneerRxApprovalCloudClient.CatalogEndpoint, request.RequestUri!.AbsolutePath);
            capturedHeaders = request.Headers.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Single(),
                StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, new
            {
                success = true,
                data = new { vendorCatalog = catalog, approvalChallenge = challenge },
            }));
        });
        using var client = Client(handler);

        var bootstrap = await client.DiscoverCatalogAsync(
            Evidence(),
            Now,
            CancellationToken.None);

        Assert.NotNull(capturedHeaders);
        foreach (var header in new[]
                 {
                     "x-agent-auth-version", "x-agent-api-key", "x-agent-timestamp",
                     "x-agent-nonce", "x-agent-content-sha256", "x-agent-signature",
                 })
            Assert.True(capturedHeaders!.ContainsKey(header));
        Assert.Equal(_maintenance.KeyId, capturedHeaders!["X-Suavo-Maintenance-Key-Id"]);
        Assert.Equal(Utc(Now), capturedHeaders["X-Suavo-Maintenance-Timestamp"]);
        Assert.Equal(string.Join('|',
            PioneerRxApprovalCloudClient.CatalogDiscoveryPrefix,
            AgentId,
            PharmacyId,
            MachineId,
            _maintenance.KeyId,
            Utc(Now)), _maintenance.LastSignedCanonical);

        var proposal = PioneerRxApprovalProposalBuilder.Build(
            Config(),
            Evidence(),
            bootstrap,
            new string('d', 64),
            "S-1-5-21-1-2-3-1001",
            "{\"accepted\":true}",
            new[] { "read" },
            _maintenance);

        Assert.Equal(challenge.ReceiptId, proposal.ReceiptId);
        Assert.Equal(challenge.ApprovalNonce, proposal.ApprovalNonce);
        Assert.Equal(challenge.ApprovalCounter, proposal.ApprovalCounter);
        Assert.Equal(challenge.ApprovedAtUtc, proposal.ApprovedAtUtc);
        Assert.Equal(challenge.ExpiresAtUtc, proposal.ExpiresAtUtc);
        Assert.Equal(new[] { "read" }, proposal.ApprovedBaaScopeTags);
        Assert.Equal(string.Empty, proposal.CloudCoApprovalSignature);
        Assert.True(VerifyMaintenanceSignature(proposal));
    }

    [Fact]
    public async Task Proposal_post_contains_only_receipt_and_accepts_only_pending_human_review()
    {
        var proposal = await BuildProposalAsync();
        using var handler = new RecordingHandler(async (request, cancellationToken) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(PioneerRxApprovalCloudClient.ApprovalEndpoint, request.RequestUri!.AbsolutePath);
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            var properties = document.RootElement.EnumerateObject().ToArray();
            Assert.Single(properties);
            Assert.Equal("receipt", properties[0].Name);
            var posted = properties[0].Value.Deserialize<PioneerRxProcessApprovalReceipt>(
                PioneerRxApprovalMaintenanceContract.JsonOptions);
            Assert.NotNull(posted);
            Assert.Equal(
                PioneerRxProcessApprovalContract.Canonical(proposal),
                PioneerRxProcessApprovalContract.Canonical(posted!));
            Assert.Equal(proposal.MaintenanceSignature, posted!.MaintenanceSignature);
            Assert.Equal(string.Empty, posted.CloudCoApprovalSignature);
            return JsonResponse(HttpStatusCode.Accepted, new
            {
                success = true,
                data = new
                {
                    proposalId = ProposalId,
                    status = "security_review_required",
                    vendorCatalogId = CatalogId,
                },
            });
        });
        using var client = Client(handler);

        var result = await client.SubmitAsync(proposal, CancellationToken.None);

        Assert.Equal(PioneerRxProposalStatus.SecurityReviewRequired, result.Status);
        Assert.Equal(ProposalId, result.ProposalId);
    }

    [Fact]
    public async Task Proposal_post_refuses_an_auto_approved_response()
    {
        var proposal = await BuildProposalAsync();
        using var handler = new RecordingHandler((_, _) => Task.FromResult(
            JsonResponse(HttpStatusCode.Accepted, new
            {
                success = true,
                data = new
                {
                    proposalId = ProposalId,
                    status = "approved",
                    vendorCatalogId = CatalogId,
                },
            })));
        using var client = Client(handler);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.SubmitAsync(proposal, CancellationToken.None));
    }

    private async Task<PioneerRxProcessApprovalReceipt> BuildProposalAsync()
    {
        var catalog = Catalog();
        var challenge = Challenge();
        using var handler = new RecordingHandler((_, _) => Task.FromResult(
            JsonResponse(HttpStatusCode.OK, new
            {
                success = true,
                data = new { vendorCatalog = catalog, approvalChallenge = challenge },
            })));
        using var client = Client(handler);
        var bootstrap = await client.DiscoverCatalogAsync(
            Evidence(),
            Now,
            CancellationToken.None);
        return PioneerRxApprovalProposalBuilder.Build(
            Config(),
            Evidence(),
            bootstrap,
            new string('d', 64),
            "S-1-5-21-1-2-3-1001",
            "{\"accepted\":true}",
            new[] { "read" },
            _maintenance);
    }

    private PioneerRxApprovalCloudClient Client(HttpMessageHandler handler) => new(
        Config(),
        _maintenance,
        handler,
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [RemoteCommandTrust.CommandV1KeyId] = Convert.ToBase64String(
                _cloud.ExportSubjectPublicKeyInfo()),
        });

    private SetupConfig Config() => new(
        PharmacyId,
        "agent-secret",
        "https://api.suavo.example",
        "v1.0.0",
        false,
        AgentId,
        MaintenanceKeyId: _maintenance.KeyId,
        DeviceFingerprint: MachineId);

    private PioneerRxVendorIdentityCatalog Catalog()
    {
        var unsigned = new PioneerRxVendorIdentityCatalog(
            PioneerRxVendorIdentityCatalogContract.SchemaVersion,
            CatalogId,
            new[]
            {
                new PioneerRxVendorIdentityEntry(
                    "dddddddd-dddd-4ddd-8ddd-dddddddddddd",
                    "PioneerPharmacy.exe",
                    "PioneerRx",
                    "CN=New Tech Computer Systems",
                    new string('c', 64),
                    new[] { @"C:\Program Files\PioneerRx\" },
                    new[] { "1.2.3.4" }),
            },
            Utc(Now.AddMinutes(-1)),
            Utc(Now.AddDays(1)),
            RemoteCommandTrust.CommandV1KeyId,
            string.Empty);
        return unsigned with
        {
            CloudSignature = SignCloud(
                PioneerRxVendorIdentityCatalogContract.Canonical(unsigned)),
        };
    }

    private static PioneerRxApprovalChallenge Challenge() => new(
        ReceiptId,
        new string('f', 64),
        42,
        Utc(Now),
        Utc(Now.AddDays(1)));

    private static PioneerRxExecutableEvidence Evidence() => new(
        "PioneerPharmacy.exe",
        @"C:\Program Files\PioneerRx\PioneerPharmacy.exe",
        new string('b', 64),
        "CN=New Tech Computer Systems",
        new string('c', 64),
        "PioneerRx",
        "1.2.3.4");

    private bool VerifyMaintenanceSignature(PioneerRxProcessApprovalReceipt proposal)
    {
        var signature = Decode(proposal.MaintenanceSignature);
        using var verifier = ECDsa.Create();
        var publicKey = Convert.FromBase64String(proposal.MaintenancePublicKeySpki);
        verifier.ImportSubjectPublicKeyInfo(publicKey, out var consumed);
        return consumed == publicKey.Length && verifier.VerifyData(
            Encoding.UTF8.GetBytes(PioneerRxProcessApprovalContract.Canonical(proposal)),
            signature,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    private string SignCloud(string canonical) =>
        PioneerRxProcessApprovalContract.Base64UrlEncode(_cloud.SignData(
            Encoding.UTF8.GetBytes(canonical),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, object value) => new(status)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(value, PioneerRxApprovalMaintenanceContract.JsonOptions),
            Encoding.UTF8,
            "application/json"),
    };

    private static byte[] Decode(string value) => Convert.FromBase64String(
        value.Replace('-', '+').Replace('_', '/') + "==");

    private static string Utc(DateTimeOffset value) =>
        value.UtcDateTime.ToString(
            PioneerRxProcessApprovalContract.UtcTimestampFormat,
            System.Globalization.CultureInfo.InvariantCulture);

    public void Dispose()
    {
        _cloud.Dispose();
        _maintenance.Dispose();
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class FakeMaintenanceKeys : IMaintenanceAttestationKeyProvider, IDisposable
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        internal string? LastSignedCanonical { get; private set; }
        internal string KeyId => Convert.ToHexString(
            SHA256.HashData(_key.ExportSubjectPublicKeyInfo())).ToLowerInvariant();
        private DeviceKeyEnrollment Enrollment => new(
            "ES256",
            KeyId,
            Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo()));

        public MaintenanceKeyRegistration OpenOrCreate(string authoritativeFingerprint) =>
            new(Enrollment, new string('p', 86));

        public MaintenanceKeyRegistration OpenExisting(string authoritativeFingerprint) =>
            new(Enrollment, new string('p', 86));

        public DeviceMaintenanceSignature Sign(
            string authoritativeFingerprint,
            string expectedKeyId,
            ReadOnlyMemory<byte> canonicalBytes)
        {
            Assert.Equal(KeyId, expectedKeyId);
            LastSignedCanonical = Encoding.UTF8.GetString(canonicalBytes.Span);
            return new DeviceMaintenanceSignature(
                Enrollment,
                _key.SignData(
                    canonicalBytes.Span,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        }

        public void DestroyForUninstall(string authoritativeFingerprint, string expectedKeyId) =>
            throw new NotSupportedException();

        public void Dispose() => _key.Dispose();
    }
}
