using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Contracts.Security;

namespace SuavoAgent.Setup.Security;

internal sealed record PioneerRxApprovalChallenge(
    [property: System.Text.Json.Serialization.JsonPropertyName("receiptId")] string ReceiptId,
    [property: System.Text.Json.Serialization.JsonPropertyName("approvalNonce")] string ApprovalNonce,
    [property: System.Text.Json.Serialization.JsonPropertyName("approvalCounter")] long ApprovalCounter,
    [property: System.Text.Json.Serialization.JsonPropertyName("approvedAtUtc")] string ApprovedAtUtc,
    [property: System.Text.Json.Serialization.JsonPropertyName("expiresAtUtc")] string ExpiresAtUtc);

internal sealed record PioneerRxVendorCatalogBootstrap(
    PioneerRxVendorIdentityCatalog VendorCatalog,
    PioneerRxApprovalChallenge ApprovalChallenge);

internal enum PioneerRxProposalStatus
{
    Pending,
    SecurityReviewRequired,
    Approved,
    Rejected,
    Revoked,
    Unknown,
}

internal sealed record PioneerRxProposalSubmission(
    PioneerRxProposalStatus Status,
    string? ProposalId = null);

internal sealed record PioneerRxProposalPollResult(
    PioneerRxProposalStatus Status,
    PioneerRxProcessApprovalReceipt? Receipt = null,
    PioneerRxApprovalAuthorityState? Authority = null,
    PioneerRxVendorIdentityCatalog? VendorCatalog = null);

/// <summary>
/// SYSTEM-side human-approval transport. Catalog discovery requires both ordinary agent HMAC and
/// a proof from the SYSTEM-only maintenance TPM key. Cloud may add only its co-signature; every
/// receipt field covered by the maintenance signature must round-trip byte-for-byte.
/// </summary>
internal sealed class PioneerRxApprovalCloudClient : IDisposable
{
    internal const string CatalogEndpoint = "/api/agent/pioneerrx/vendor-catalog";
    internal const string ApprovalEndpoint = "/api/agent/pioneerrx/process-approval";
    internal const string CatalogDiscoveryPrefix =
        "suavo.pioneerrx-vendor-catalog-discovery.v1";
    private const int MaximumResponseBytes = 512 * 1024;
    private readonly SetupConfig _config;
    private readonly HttpClient _http;
    private readonly AgentRequestSigner _requestSigner;
    private readonly IMaintenanceAttestationKeyProvider _maintenanceKeys;
    private readonly IReadOnlyDictionary<string, string> _trustedCloudKeys;

    internal PioneerRxApprovalCloudClient(
        SetupConfig config,
        IMaintenanceAttestationKeyProvider maintenanceKeys,
        HttpMessageHandler? handler = null,
        IReadOnlyDictionary<string, string>? trustedCloudKeys = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _maintenanceKeys = maintenanceKeys ?? throw new ArgumentNullException(nameof(maintenanceKeys));
        if (!TryCloudOrigin(config.CloudUrl, out var origin) ||
            string.IsNullOrWhiteSpace(config.ApiKey) ||
            !CanonicalUuid(config.AgentId) ||
            !CanonicalUuid(config.PharmacyId) ||
            !CanonicalUuid(config.DeviceFingerprint) ||
            !LowerHex64(config.MaintenanceKeyId))
            throw new InvalidDataException("PioneerRx approval cloud identity is invalid.");
        handler ??= new HttpClientHandler { AllowAutoRedirect = false };
        if (handler is HttpClientHandler httpHandler) httpHandler.AllowAutoRedirect = false;
        _http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri(origin!, "/"),
            Timeout = TimeSpan.FromSeconds(20),
        };
        _requestSigner = new AgentRequestSigner(config.ApiKey);
        _trustedCloudKeys = trustedCloudKeys ?? RemoteCommandTrust.CreateProductionKeyRegistry();
    }

    internal async Task<PioneerRxVendorCatalogBootstrap> DiscoverCatalogAsync(
        PioneerRxExecutableEvidence evidence,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var timestamp = Utc(now);
        var registration = _maintenanceKeys.OpenExisting(_config.DeviceFingerprint!);
        if (!string.Equals(
                registration.Enrollment.KeyId,
                _config.MaintenanceKeyId,
                StringComparison.Ordinal))
            throw new InvalidDataException("Maintenance key identity changed before catalog discovery.");
        var canonical = string.Join('|',
            CatalogDiscoveryPrefix,
            _config.AgentId,
            _config.PharmacyId,
            _config.DeviceFingerprint,
            _config.MaintenanceKeyId,
            timestamp);
        var canonicalBytes = Encoding.UTF8.GetBytes(canonical);
        DeviceMaintenanceSignature proof;
        try
        {
            proof = _maintenanceKeys.Sign(
                _config.DeviceFingerprint!,
                _config.MaintenanceKeyId!,
                canonicalBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonicalBytes);
        }
        if (!string.Equals(
                proof.Enrollment.KeyId,
                _config.MaintenanceKeyId,
                StringComparison.Ordinal) ||
            proof.Signature.Length != 64)
            throw new CryptographicException("Maintenance catalog proof is invalid.");

        using var request = new HttpRequestMessage(HttpMethod.Get, CatalogEndpoint);
        _requestSigner.ApplyHeaders(request, string.Empty);
        AddHeader(request, "X-Suavo-Maintenance-Key-Id", _config.MaintenanceKeyId!);
        AddHeader(request, "X-Suavo-Maintenance-Timestamp", timestamp);
        AddHeader(
            request,
            "X-Suavo-Maintenance-Signature",
            PioneerRxProcessApprovalContract.Base64UrlEncode(proof.Signature.Span));
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                "PioneerRx vendor catalog discovery was rejected.",
                null,
                response.StatusCode);
        var root = await BoundedJsonResponse.ReadObjectAsync(
            response.Content,
            MaximumResponseBytes,
            cancellationToken).ConfigureAwait(false);
        var data = RequireSuccessData(root, expectedDataFields: 2);
        var catalog = DeserializeExact<PioneerRxVendorIdentityCatalog>(data, "vendorCatalog");
        var challenge = DeserializeExact<PioneerRxApprovalChallenge>(data, "approvalChallenge");
        if (!PioneerRxVendorIdentityCatalogContract.TryValidate(
                catalog,
                now,
                _trustedCloudKeys,
                out var code) ||
            !PioneerRxVendorIdentityCatalogContract.TryMatchEvidence(
                catalog,
                evidence.ProcessName,
                evidence.ProductName,
                evidence.AuthenticodeSignerSubject,
                evidence.SignerCertificateSha256,
                evidence.CanonicalExecutablePath,
                evidence.FileVersion,
                out code))
            throw new InvalidDataException(StableCode(code, "approval_vendor_catalog_invalid"));
        ValidateChallenge(challenge, now);
        return new(catalog, challenge);
    }

    internal async Task<PioneerRxProposalSubmission> SubmitAsync(
        PioneerRxProcessApprovalReceipt proposal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var body = JsonSerializer.Serialize(
            new { receipt = proposal },
            PioneerRxApprovalMaintenanceContract.JsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, ApprovalEndpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        _requestSigner.ApplyHeaders(request, body);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Accepted)
            return response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or
                HttpStatusCode.Forbidden or HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity
                ? new(PioneerRxProposalStatus.Rejected)
                : new(PioneerRxProposalStatus.Unknown);
        var root = await BoundedJsonResponse.ReadObjectAsync(
            response.Content,
            MaximumResponseBytes,
            cancellationToken).ConfigureAwait(false);
        var data = RequireSuccessData(root, expectedDataFields: 3);
        var proposalId = RequireCanonicalUuid(data, "proposalId");
        var status = RequireString(data, "status") switch
        {
            "pending" => PioneerRxProposalStatus.Pending,
            "security_review_required" => PioneerRxProposalStatus.SecurityReviewRequired,
            _ => throw new InvalidDataException("Proposal response status is invalid."),
        };
        var catalogId = RequireCanonicalUuid(data, "vendorCatalogId");
        if (!string.Equals(catalogId, proposal.VendorCatalogId, StringComparison.Ordinal))
            throw new InvalidDataException("Proposal response changed the vendor catalog binding.");
        return new(status, proposalId);
    }

    internal async Task<PioneerRxProposalPollResult> PollAsync(
        PioneerRxProcessApprovalReceipt proposal,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        using var request = new HttpRequestMessage(HttpMethod.Get, ApprovalEndpoint);
        _requestSigner.ApplyHeaders(request, string.Empty);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new(PioneerRxProposalStatus.Pending);
        if (!response.IsSuccessStatusCode)
            return response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or
                HttpStatusCode.Forbidden or HttpStatusCode.Gone
                ? new(PioneerRxProposalStatus.Rejected)
                : new(PioneerRxProposalStatus.Unknown);
        var root = await BoundedJsonResponse.ReadObjectAsync(
            response.Content,
            MaximumResponseBytes,
            cancellationToken).ConfigureAwait(false);
        var data = RequireSuccessData(root, expectedDataFields: 3);
        var receipt = DeserializeExact<PioneerRxProcessApprovalReceipt>(data, "receipt");
        var authority = DeserializeExact<PioneerRxApprovalAuthorityState>(data, "authority");
        var catalog = DeserializeExact<PioneerRxVendorIdentityCatalog>(data, "vendorCatalog");
        if (!string.Equals(
                PioneerRxProcessApprovalContract.Canonical(receipt),
                PioneerRxProcessApprovalContract.Canonical(proposal),
                StringComparison.Ordinal) ||
            !string.Equals(
                receipt.MaintenancePublicKeySpki,
                proposal.MaintenancePublicKeySpki,
                StringComparison.Ordinal) ||
            !string.Equals(
                receipt.MaintenanceSignature,
                proposal.MaintenanceSignature,
                StringComparison.Ordinal) ||
            !string.Equals(catalog.CatalogId, proposal.VendorCatalogId, StringComparison.Ordinal))
            throw new InvalidDataException("Cloud approval changed maintenance-signed proposal fields.");

        var revoked = PioneerRxProcessApprovalContract.IsReceiptRevoked(authority, receipt.ReceiptId);
        var receiptAuthentic = revoked
            ? PioneerRxProcessApprovalContract.TryValidateHistoricalForRevocation(
                receipt,
                catalog,
                _config.PharmacyId,
                _config.DeviceFingerprint!,
                _config.MaintenanceKeyId!,
                now,
                _trustedCloudKeys,
                out var code)
            : PioneerRxProcessApprovalContract.TryValidate(
                receipt,
                catalog,
                _config.PharmacyId,
                _config.DeviceFingerprint!,
                _config.MaintenanceKeyId!,
                now,
                _trustedCloudKeys,
                out code);
        if (!receiptAuthentic ||
            !PioneerRxProcessApprovalContract.TryValidateAuthorityDocument(
                authority,
                _config.PharmacyId,
                _config.DeviceFingerprint!,
                now,
                _trustedCloudKeys,
                out code) ||
            !string.Equals(authority.ActiveReceiptId, receipt.ReceiptId, StringComparison.Ordinal) ||
            authority.CurrentApprovalCounter != receipt.ApprovalCounter)
            throw new InvalidDataException(StableCode(code, "approval_poll_invalid"));
        return new(
            revoked ? PioneerRxProposalStatus.Revoked : PioneerRxProposalStatus.Approved,
            receipt,
            authority,
            catalog);
    }

    private static JsonObject RequireSuccessData(JsonObject root, int expectedDataFields)
    {
        if (root.Count != 2 || root["success"]?.GetValue<bool>() != true ||
            root["data"] is not JsonObject data || data.Count != expectedDataFields)
            throw new InvalidDataException("PioneerRx approval response shape is invalid.");
        return data;
    }

    private static T DeserializeExact<T>(JsonObject data, string name)
    {
        if (data[name] is not JsonObject value)
            throw new InvalidDataException($"PioneerRx approval response is missing {name}.");
        return value.Deserialize<T>(PioneerRxApprovalMaintenanceContract.JsonOptions)
               ?? throw new InvalidDataException($"PioneerRx approval response {name} is empty.");
    }

    private static void ValidateChallenge(
        PioneerRxApprovalChallenge challenge,
        DateTimeOffset now)
    {
        if (!CanonicalUuid(challenge.ReceiptId) ||
            !LowerHex64(challenge.ApprovalNonce) ||
            challenge.ApprovalCounter <= 0 ||
            !TryUtc(challenge.ApprovedAtUtc, out var approvedAt) ||
            !TryUtc(challenge.ExpiresAtUtc, out var expiresAt) ||
            approvedAt > now.AddMinutes(5) || expiresAt <= approvedAt ||
            expiresAt - approvedAt > PioneerRxProcessApprovalContract.MaximumApprovalLifetime ||
            now >= expiresAt)
            throw new InvalidDataException("PioneerRx approval challenge is invalid.");
    }

    private static void AddHeader(HttpRequestMessage request, string name, string value)
    {
        if (request.Headers.Contains(name) ||
            !request.Headers.TryAddWithoutValidation(name, value))
            throw new InvalidOperationException("PioneerRx maintenance proof header is invalid.");
    }

    private static string RequireString(JsonObject data, string name) =>
        data[name]?.GetValue<string>()
        ?? throw new InvalidDataException($"PioneerRx approval response is missing {name}.");

    private static string RequireCanonicalUuid(JsonObject data, string name)
    {
        var value = RequireString(data, name);
        return CanonicalUuid(value)
            ? value
            : throw new InvalidDataException($"PioneerRx approval response {name} is invalid.");
    }

    private static bool TryCloudOrigin(string? value, out Uri? origin)
    {
        origin = null;
        if (!Uri.TryCreate(value?.TrimEnd('/'), UriKind.Absolute, out var parsed) ||
            parsed.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(parsed.UserInfo) ||
            !string.IsNullOrEmpty(parsed.Query) || !string.IsNullOrEmpty(parsed.Fragment) ||
            parsed.AbsolutePath is not ("" or "/"))
            return false;
        origin = new Uri(parsed.GetLeftPart(UriPartial.Authority), UriKind.Absolute);
        return true;
    }

    private static string Utc(DateTimeOffset value) =>
        value.UtcDateTime.ToString(
            PioneerRxProcessApprovalContract.UtcTimestampFormat,
            System.Globalization.CultureInfo.InvariantCulture);

    private static bool TryUtc(string? value, out DateTimeOffset parsed) =>
        DateTimeOffset.TryParseExact(
            value,
            PioneerRxProcessApprovalContract.UtcTimestampFormat,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal |
            System.Globalization.DateTimeStyles.AdjustToUniversal,
            out parsed);

    private static bool CanonicalUuid(string? value) =>
        value is { Length: 36 } && Guid.TryParseExact(value, "D", out var parsed) &&
        string.Equals(parsed.ToString("D"), value, StringComparison.Ordinal);

    private static bool LowerHex64(string? value) =>
        value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string StableCode(string? value, string fallback) =>
        value is { Length: > 0 and <= 80 } &&
        value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_')
            ? value
            : fallback;

    public void Dispose() => _http.Dispose();
}
