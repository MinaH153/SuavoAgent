using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Contracts.Security;
using SuavoAgent.Setup.Security;
using Xunit;

namespace SuavoAgent.Setup.Tests.Security;

public sealed class PioneerRxApprovalCloudFailureTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-07-12T12:00:00Z");

    [Theory]
    [InlineData("")]
    [InlineData("http://api.suavo.example")]
    [InlineData("https://user@api.suavo.example")]
    [InlineData("https://api.suavo.example/path")]
    [InlineData("https://api.suavo.example?query=1")]
    [InlineData("https://api.suavo.example#fragment")]
    public void Constructor_RejectsCloudUrlThatIsNotExactHttpsOrigin(string cloudUrl)
    {
        using var keys = new FakeKeys();
        Assert.Throws<InvalidDataException>(() =>
            new PioneerRxApprovalCloudClient(Config(keys, cloudUrl: cloudUrl), keys));
    }

    [Fact]
    public void Constructor_RejectsMissingDependenciesAndIdentityFields()
    {
        using var keys = new FakeKeys();
        Assert.Throws<ArgumentNullException>(() =>
            new PioneerRxApprovalCloudClient(null!, keys));
        Assert.Throws<ArgumentNullException>(() =>
            new PioneerRxApprovalCloudClient(Config(keys), null!));

        foreach (var invalid in new[]
                 {
                     Config(keys) with { ApiKey = "" },
                     Config(keys) with { AgentId = "not-a-uuid" },
                     Config(keys) with { PharmacyId = "not-a-uuid" },
                     Config(keys) with { DeviceFingerprint = "not-a-uuid" },
                     Config(keys) with { MaintenanceKeyId = new string('A', 64) },
                 })
        {
            Assert.Throws<InvalidDataException>(() =>
                new PioneerRxApprovalCloudClient(invalid, keys));
        }
    }

    [Fact]
    public async Task CatalogDiscovery_RejectsNullEvidenceAndChangedMaintenanceKey()
    {
        using var keys = new FakeKeys { ReturnDifferentEnrollment = true };
        using var client = Client(keys, _ => Json(HttpStatusCode.OK, new { }));

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            client.DiscoverCatalogAsync(null!, Now, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.DiscoverCatalogAsync(Evidence(), Now, CancellationToken.None));
    }

    [Fact]
    public async Task CatalogDiscovery_RejectsMalformedMaintenanceSignature()
    {
        using var keys = new FakeKeys { ShortSignature = true };
        using var client = Client(keys, _ => Json(HttpStatusCode.OK, new { }));

        await Assert.ThrowsAsync<CryptographicException>(() =>
            client.DiscoverCatalogAsync(Evidence(), Now, CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task CatalogDiscovery_PropagatesRejectedHttpStatus(HttpStatusCode status)
    {
        using var keys = new FakeKeys();
        using var client = Client(keys, _ => new HttpResponseMessage(status));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.DiscoverCatalogAsync(Evidence(), Now, CancellationToken.None));
        Assert.Equal(status, error.StatusCode);
    }

    [Fact]
    public async Task CatalogDiscovery_RejectsNonExactEnvelopeBeforeCatalogUse()
    {
        using var keys = new FakeKeys();
        using var client = Client(keys, _ => Json(HttpStatusCode.OK, new
        {
            success = true,
            data = new { vendorCatalog = new { } },
        }));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.DiscoverCatalogAsync(Evidence(), Now, CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, (int)PioneerRxProposalStatus.Rejected)]
    [InlineData(HttpStatusCode.Unauthorized, (int)PioneerRxProposalStatus.Rejected)]
    [InlineData(HttpStatusCode.Forbidden, (int)PioneerRxProposalStatus.Rejected)]
    [InlineData(HttpStatusCode.Conflict, (int)PioneerRxProposalStatus.Rejected)]
    [InlineData(HttpStatusCode.UnprocessableEntity, (int)PioneerRxProposalStatus.Rejected)]
    [InlineData(HttpStatusCode.NotFound, (int)PioneerRxProposalStatus.Unknown)]
    [InlineData(HttpStatusCode.InternalServerError, (int)PioneerRxProposalStatus.Unknown)]
    public async Task Submit_ClassifiesNonAcceptedStatusWithoutTrustingBody(
        HttpStatusCode status,
        int expectedStatus)
    {
        using var keys = new FakeKeys();
        using var client = Client(keys, _ => new HttpResponseMessage(status));

        var result = await client.SubmitAsync(Proposal(), CancellationToken.None);

        Assert.Equal((PioneerRxProposalStatus)expectedStatus, result.Status);
        Assert.Null(result.ProposalId);
    }

    [Fact]
    public async Task Submit_AcceptsExactPendingShapeAndCatalogBinding()
    {
        using var keys = new FakeKeys();
        using var client = Client(keys, _ => Json(HttpStatusCode.Accepted, new
        {
            success = true,
            data = new
            {
                proposalId = ProposalId,
                status = "pending",
                vendorCatalogId = CatalogId,
            },
        }));

        var result = await client.SubmitAsync(Proposal(), CancellationToken.None);

        Assert.Equal(PioneerRxProposalStatus.Pending, result.Status);
        Assert.Equal(ProposalId, result.ProposalId);
    }

    [Theory]
    [InlineData("not-a-uuid", "pending", CatalogId)]
    [InlineData(ProposalId, "approved", CatalogId)]
    [InlineData(ProposalId, "pending", "99999999-9999-4999-8999-999999999999")]
    public async Task Submit_RejectsChangedIdentityStatusOrCatalog(
        string proposalId,
        string status,
        string catalogId)
    {
        using var keys = new FakeKeys();
        using var client = Client(keys, _ => Json(HttpStatusCode.Accepted, new
        {
            success = true,
            data = new { proposalId, status, vendorCatalogId = catalogId },
        }));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.SubmitAsync(Proposal(), CancellationToken.None));
    }

    [Fact]
    public async Task Submit_RejectsNullProposalAndMalformedEnvelope()
    {
        using var keys = new FakeKeys();
        using var client = Client(keys, _ => Json(HttpStatusCode.Accepted, new
        {
            success = false,
            data = new { },
        }));

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            client.SubmitAsync(null!, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.SubmitAsync(Proposal(), CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, (int)PioneerRxProposalStatus.Pending)]
    [InlineData(HttpStatusCode.BadRequest, (int)PioneerRxProposalStatus.Rejected)]
    [InlineData(HttpStatusCode.Unauthorized, (int)PioneerRxProposalStatus.Rejected)]
    [InlineData(HttpStatusCode.Forbidden, (int)PioneerRxProposalStatus.Rejected)]
    [InlineData(HttpStatusCode.Gone, (int)PioneerRxProposalStatus.Rejected)]
    [InlineData(HttpStatusCode.Conflict, (int)PioneerRxProposalStatus.Unknown)]
    [InlineData(HttpStatusCode.InternalServerError, (int)PioneerRxProposalStatus.Unknown)]
    public async Task Poll_ClassifiesAbsentRejectedAndTransientStatuses(
        HttpStatusCode status,
        int expectedStatus)
    {
        using var keys = new FakeKeys();
        using var client = Client(keys, _ => new HttpResponseMessage(status));

        var result = await client.PollAsync(Proposal(), Now, CancellationToken.None);

        Assert.Equal((PioneerRxProposalStatus)expectedStatus, result.Status);
        Assert.Null(result.Receipt);
        Assert.Null(result.Authority);
        Assert.Null(result.VendorCatalog);
    }

    [Fact]
    public async Task Poll_RejectsNullProposalAndMissingSignedObjects()
    {
        using var keys = new FakeKeys();
        using var client = Client(keys, _ => Json(HttpStatusCode.OK, new
        {
            success = true,
            data = new { receipt = new { }, authority = new { }, vendorCatalog = new { } },
        }));

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            client.PollAsync(null!, Now, CancellationToken.None));
        await Assert.ThrowsAnyAsync<Exception>(() =>
            client.PollAsync(Proposal(), Now, CancellationToken.None));
    }

    [Theory]
    [InlineData("https://api.suavo.example", true)]
    [InlineData("https://api.suavo.example/", true)]
    [InlineData("HTTPs://API.SUAVO.EXAMPLE", true)]
    [InlineData("http://api.suavo.example", false)]
    [InlineData("https://api.suavo.example/path", false)]
    [InlineData(null, false)]
    public void CloudOriginParser_IsExact(string? value, bool expected)
    {
        var arguments = new object?[] { value, null };
        var result = (bool)Method("TryCloudOrigin").Invoke(null, arguments)!;
        Assert.Equal(expected, result);
        Assert.Equal(expected, arguments[1] is Uri);
    }

    [Theory]
    [InlineData("approval_poll_invalid", "fallback", "approval_poll_invalid")]
    [InlineData("", "fallback", "fallback")]
    [InlineData("HAS-UPPER", "fallback", "fallback")]
    [InlineData("contains-hyphen", "fallback", "fallback")]
    public void StableCode_AdmitsOnlyBoundedMachineSafeCodes(
        string value,
        string fallback,
        string expected)
    {
        Assert.Equal(expected, (string)Method("StableCode").Invoke(
            null,
            [value, fallback])!);
    }

    private static PioneerRxApprovalCloudClient Client(
        FakeKeys keys,
        Func<HttpRequestMessage, HttpResponseMessage> response) => new(
        Config(keys),
        keys,
        new Handler(response),
        new Dictionary<string, string>());

    private static SetupConfig Config(FakeKeys keys, string cloudUrl = "https://api.suavo.example") => new(
        PharmacyId,
        "agent-secret",
        cloudUrl,
        "v3.80.0",
        false,
        AgentId,
        MaintenanceKeyId: keys.KeyId,
        DeviceFingerprint: MachineId);

    private static PioneerRxProcessApprovalReceipt Proposal() => new(
        PioneerRxProcessApprovalContract.CurrentSchemaVersion,
        "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
        AgentId,
        PharmacyId,
        new string('a', 64),
        "spki",
        "PioneerPharmacy.exe",
        @"C:\Program Files\PioneerRx\PioneerPharmacy.exe",
        new string('b', 64),
        "CN=New Tech Computer Systems",
        new string('c', 64),
        "PioneerRx",
        "1.2.3.4",
        CatalogId,
        new string('d', 64),
        "S-1-5-21-1-2-3-1001",
        new string('e', 64),
        new string('f', 64),
        1,
        new[] { "read" },
        Utc(Now),
        Utc(Now.AddDays(1)),
        null,
        "suavo-cmd-v1",
        string.Empty,
        string.Empty);

    private static PioneerRxExecutableEvidence Evidence() => new(
        "PioneerPharmacy.exe",
        @"C:\Program Files\PioneerRx\PioneerPharmacy.exe",
        new string('b', 64),
        "CN=New Tech Computer Systems",
        new string('c', 64),
        "PioneerRx",
        "1.2.3.4");

    private static MethodInfo Method(string name)
    {
        var method = typeof(PioneerRxApprovalCloudClient).GetMethod(
            name,
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return method;
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object value) => new(status)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(value, PioneerRxApprovalMaintenanceContract.JsonOptions),
            Encoding.UTF8,
            "application/json"),
    };

    private static string Utc(DateTimeOffset value) =>
        value.UtcDateTime.ToString(
            PioneerRxProcessApprovalContract.UtcTimestampFormat,
            System.Globalization.CultureInfo.InvariantCulture);

    private const string AgentId = "11111111-1111-4111-8111-111111111111";
    private const string PharmacyId = "22222222-2222-4222-8222-222222222222";
    private const string MachineId = "33333333-3333-4333-8333-333333333333";
    private const string ProposalId = "44444444-4444-4444-8444-444444444444";
    private const string CatalogId = "55555555-5555-4555-8555-555555555555";

    private sealed class Handler(
        Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(response(request));
    }

    private sealed class FakeKeys : IMaintenanceAttestationKeyProvider, IDisposable
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        internal bool ReturnDifferentEnrollment { get; init; }
        internal bool ShortSignature { get; init; }
        internal string KeyId => Enrollment.KeyId;
        private DeviceKeyEnrollment Enrollment
        {
            get
            {
                var spki = _key.ExportSubjectPublicKeyInfo();
                return new(
                    "ES256",
                    Convert.ToHexString(SHA256.HashData(spki)).ToLowerInvariant(),
                    Convert.ToBase64String(spki));
            }
        }

        public MaintenanceKeyRegistration OpenOrCreate(string authoritativeFingerprint) =>
            OpenExisting(authoritativeFingerprint);

        public MaintenanceKeyRegistration OpenExisting(string authoritativeFingerprint)
        {
            var enrollment = ReturnDifferentEnrollment
                ? Enrollment with { KeyId = new string('0', 64) }
                : Enrollment;
            return new(enrollment, new string('p', 86));
        }

        public DeviceMaintenanceSignature Sign(
            string authoritativeFingerprint,
            string expectedKeyId,
            ReadOnlyMemory<byte> canonicalBytes)
        {
            var signature = ShortSignature
                ? new byte[1]
                : _key.SignData(
                    canonicalBytes.Span,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return new(Enrollment, signature);
        }

        public void DestroyForUninstall(
            string authoritativeFingerprint,
            string expectedKeyId) => throw new NotSupportedException();

        public void Dispose() => _key.Dispose();
    }
}
