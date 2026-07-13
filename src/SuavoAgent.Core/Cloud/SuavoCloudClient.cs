using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Contracts.Models;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.State;
using SuavoAgent.Core.Workers;

namespace SuavoAgent.Core.Cloud;

public sealed partial class SuavoCloudClient : IPostSigner, IDisposable
{
    private const int MaxSignedResponseBytes = 128 * 1024;
    private const int MaxErrorResponseBytes = 16 * 1024;
    private sealed record VerifiedResponse(
        JsonElement Body,
        string KeyId,
        string SignatureBase64,
        string CanonicalBodySha256,
        string CanonicalBodyJson);

    private static readonly Regex OffsetTimestamp = new(
        @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly HttpClient _http;
    private readonly HmacSigner _signer;
    private readonly AgentOptions _options;
    private readonly string _callbackResponsePublicKeyDer;

    public string? BoundAgentInstanceId => _options.AgentId;
    public string? BoundPharmacyId => _options.PharmacyId;

    public SuavoCloudClient(AgentOptions options)
        : this(options, CreateHandler(options))
    {
    }

    internal SuavoCloudClient(
        AgentOptions options,
        HttpMessageHandler handler,
        string? callbackResponsePublicKeyDer = null)
    {
        _options = options;
        _signer = new HmacSigner(options.ApiKey ?? throw new InvalidOperationException("ApiKey is required"));
        _callbackResponsePublicKeyDer = callbackResponsePublicKeyDer
            ?? RemoteCommandTrust.CommandV1PublicKeyDer;

        var uri = new Uri(options.CloudUrl);
        if (uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException($"CloudUrl must use HTTPS, got: {uri.Scheme}");

        _http = new HttpClient(handler) { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(30) };
    }

    private static HttpMessageHandler CreateHandler(AgentOptions options)
    {
        // A redirect is a different request target and therefore requires a
        // new signature/nonce. HttpClient's transparent redirect path cannot
        // re-sign, so credentialed agent transports reject redirects instead.
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        if (!string.IsNullOrEmpty(options.CloudCertPin))
        {
            handler.ServerCertificateCustomValidationCallback = (message, cert, chain, errors) =>
            {
                if (errors != System.Net.Security.SslPolicyErrors.None) return false;
                if (cert == null) return false;
                var certHash = Convert.ToBase64String(
                    System.Security.Cryptography.SHA256.HashData(cert.GetPublicKey()));
                var pins = options.CloudCertPin!.Split(';', StringSplitOptions.RemoveEmptyEntries);
                return pins.Any(pin => pin.Equals(certHash, StringComparison.Ordinal));
            };
        }
        return handler;
    }

    public async Task<JsonElement?> HeartbeatAsync(object payload, CancellationToken ct)
    {
        return await PostSignedAsync("/api/agent/heartbeat", payload, ct);
    }

    public async Task<JsonElement?> SyncRxAsync(object payload, CancellationToken ct)
    {
        return await PostSignedAsync("/api/agent/sync", payload, ct);
    }

    internal async Task<bool> SyncRxDeviceBoundAsync(
        object payload,
        SignedDeviceReceipt<RxSourceDeviceReceipt> signed,
        CancellationToken ct)
    {
        var response = await PostSignedAsync("/api/agent/sync", payload, ct)
            .ConfigureAwait(false);
        if (response is null || response.Value.ValueKind != JsonValueKind.Object ||
            !response.Value.TryGetProperty("success", out var success) ||
            success.ValueKind != JsonValueKind.True ||
            !response.Value.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("stored", out var stored) ||
            stored.ValueKind != JsonValueKind.True ||
            !TryReadString(data, "batchDigest", out var batchDigest) ||
            !TryReadString(data, "sourceKeyId", out var keyId) ||
            !TryReadString(data, "sourceBindingId", out var sourceBindingId) ||
            !data.TryGetProperty("sourceCounter", out var counter) ||
            counter.ValueKind != JsonValueKind.Number ||
            !counter.TryGetInt64(out var sourceCounter))
            return false;
        return string.Equals(
                   batchDigest,
                   signed.Receipt.BatchDigest,
                   StringComparison.Ordinal) &&
               string.Equals(keyId, signed.KeyId, StringComparison.Ordinal) &&
               string.Equals(
                   sourceBindingId,
                   signed.Receipt.SourceBindingId,
                   StringComparison.Ordinal) &&
               sourceCounter == signed.Receipt.Counter;
    }

    internal sealed record PomActivationCloudReceipt(
        string CommandId,
        string Status,
        string? SourceBindingId,
        bool Idempotent);

    internal async Task<PomActivationCloudReceipt?> SendPomActivationReceiptAsync(
        SignedDeviceReceipt<PomActivationDeviceReceipt> signed,
        CancellationToken ct)
    {
        var response = await PostSignedAsync(
            "/api/agent/pom/activation-receipt",
            new
            {
                receipt = JsonSerializer.SerializeToElement(
                    signed.Receipt,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                keyId = signed.KeyId,
                signature = signed.Signature,
            },
            ct).ConfigureAwait(false);
        if (response is null || response.Value.ValueKind != JsonValueKind.Object ||
            !response.Value.TryGetProperty("success", out var success) ||
            success.ValueKind != JsonValueKind.True ||
            !response.Value.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object ||
            !TryReadString(data, "commandId", out var commandId) ||
            !TryReadString(data, "status", out var status) ||
            !data.TryGetProperty("idempotent", out var idempotent) ||
            idempotent.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return null;
        string? sourceBindingId = null;
        if (data.TryGetProperty("sourceBindingId", out var source) &&
            source.ValueKind == JsonValueKind.String)
            sourceBindingId = source.GetString();
        if (!string.Equals(commandId, signed.Receipt.CommandId, StringComparison.Ordinal) ||
            status is not ("executed" or "failed") ||
            (status == "executed" && !IsCanonicalUuid(sourceBindingId ?? "")))
            return null;
        return new(commandId, status, sourceBindingId, idempotent.GetBoolean());
    }

    /// <summary>
    /// Sends the approved, minimum-necessary delivery fields for one exact hash-only candidate.
    /// The raw Rx number is not accepted by this API and therefore cannot cross the cloud boundary.
    /// A successful HTTP status is insufficient: the exact response bytes must carry a valid
    /// command-key signature and the receipt must bind the command/candidate/pharmacy identifiers.
    /// </summary>
    internal async Task<PatientDetailsCallbackReceipt?> SendApprovedPatientDetailsAsync(
        ApprovedPatientFetchCommand command,
        PatientDetailsPayload details,
        CancellationToken ct)
    {
        if (!_options.EnableAuditedPatientDetailsEgress)
        {
            Serilog.Log.Warning(
                "patient-details egress is fail-closed (EnableAuditedPatientDetailsEgress=false); "
                + "approved patient PHI was NOT sent (commandId {CommandId}).",
                command.CommandId);
            return null;
        }

        // Explicit lower-camel projection keeps the Windows serializer byte contract aligned with
        // the route's strict Zod schema. The typed PatientDetailsPayload remains the allow-list;
        // no opaque object and no RxNumber can be smuggled into this body.
        var callback = new
        {
            schemaVersion = 1,
            commandId = command.CommandId,
            candidateId = command.CandidateId,
            rxHash = command.RxHash,
            evidenceId = command.EvidenceId,
            pharmacyId = command.PharmacyId,
            details = new
            {
                firstName = details.FirstName,
                lastInitial = details.LastInitial,
                phone = details.Phone,
                address1 = details.Address1,
                address2 = details.Address2,
                city = details.City,
                state = details.State,
                zip = details.Zip,
            },
        };

        var response = await PostSignedVerifiedCoreAsync(
            "/api/agent/patient-details",
            callback,
            _callbackResponsePublicKeyDer,
            allowTypedPatientDetails: true,
            ct).ConfigureAwait(false);
        if (response is null) return null;
        return TryParsePatientDetailsReceipt(response.Value, command, out var receipt)
            ? receipt
            : null;
    }

    internal static bool TryParsePatientDetailsReceipt(
        JsonElement response,
        ApprovedPatientFetchCommand command,
        out PatientDetailsCallbackReceipt? receipt,
        DateTimeOffset? nowUtc = null)
    {
        receipt = null;
        if (response.ValueKind != JsonValueKind.Object ||
            !HasExactProperties(response, "success", "data") ||
            !response.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True ||
            !response.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
            !HasExactProperties(
                data,
                "schemaVersion", "commandId", "candidateId", "pharmacyId", "stagingId",
                "transitionId", "status", "reviewState", "expiresAt", "idempotent"))
            return false;

        if (!data.TryGetProperty("schemaVersion", out var schema) ||
            schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var schemaVersion) || schemaVersion != 1 ||
            !TryReadString(data, "commandId", out var commandId) ||
            !TryReadString(data, "candidateId", out var candidateId) ||
            !TryReadString(data, "pharmacyId", out var pharmacyId) ||
            !TryReadString(data, "stagingId", out var stagingId) ||
            !TryReadString(data, "transitionId", out var transitionId) ||
            !TryReadString(data, "status", out var status) ||
            !TryReadString(data, "reviewState", out var reviewState) ||
            !TryReadString(data, "expiresAt", out var expiresAtRaw) ||
            !data.TryGetProperty("idempotent", out var idempotentElement) ||
            idempotentElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return false;

        if (!string.Equals(commandId, command.CommandId, StringComparison.Ordinal) ||
            !string.Equals(candidateId, command.CandidateId, StringComparison.Ordinal) ||
            !string.Equals(pharmacyId, command.PharmacyId, StringComparison.Ordinal) ||
            !IsCanonicalUuid(stagingId) || !IsCanonicalUuid(transitionId) ||
            !string.Equals(status, "patient_details_received", StringComparison.Ordinal) ||
            reviewState is not ("ready_for_review" or "needs_review") ||
            !DateTimeOffset.TryParse(expiresAtRaw, out var expiresAt))
            return false;

        var now = nowUtc ?? DateTimeOffset.UtcNow;
        if (expiresAt <= now - TimeSpan.FromSeconds(30) ||
            expiresAt > now + TimeSpan.FromMinutes(31))
            return false;

        receipt = new PatientDetailsCallbackReceipt(
            commandId,
            candidateId,
            pharmacyId,
            stagingId,
            transitionId,
            status,
            reviewState,
            expiresAt,
            idempotentElement.GetBoolean());
        return true;
    }

    internal async Task<DeliveryWritebackCallbackReceipt?> SendDeliveryWritebackAsync(
        AgentDeliveryWritebackCommand command,
        DeliveryWritebackResultCode resultCode,
        CancellationToken ct)
    {
        var callback = new
        {
            schemaVersion = command.SchemaVersion,
            writebackId = command.WritebackId,
            commandId = command.CommandId,
            candidateId = command.CandidateId,
            rxHash = command.RxHash,
            evidenceId = command.EvidenceId,
            pharmacyId = command.PharmacyId,
            orderId = command.OrderId,
            inboxItemId = command.InboxItemId,
            pmsReferenceId = command.PmsReferenceId,
            proofRecordId = command.ProofRecordId,
            proofDigest = command.ProofDigest,
            transition = command.Transition,
            transitionAt = command.TransitionAt,
            resultCode = resultCode.ToWireValue(),
        };
        var response = await SendSignedVerifiedEnvelopeCoreAsync(
            HttpMethod.Patch,
            "/api/agent/delivery-writeback",
            callback,
            _callbackResponsePublicKeyDer,
            RemoteCommandTrust.CommandV1KeyId,
            allowTypedPatientDetails: false,
            ct).ConfigureAwait(false);
        if (response is null) return null;
        return TryParseDeliveryWritebackReceipt(
            response.Body,
            command,
            resultCode,
            out var receipt,
            new DeliveryWritebackSignedProof(
                response.KeyId,
                response.SignatureBase64,
                response.CanonicalBodySha256,
                response.CanonicalBodyJson))
            ? receipt
            : null;
    }

    internal static bool TryParseDeliveryWritebackReceipt(
        JsonElement response,
        AgentDeliveryWritebackCommand command,
        DeliveryWritebackResultCode expectedResult,
        out DeliveryWritebackCallbackReceipt? receipt,
        DeliveryWritebackSignedProof? proof = null)
    {
        receipt = null;
        if (response.ValueKind != JsonValueKind.Object ||
            !HasExactProperties(response, "success", "data") ||
            !response.TryGetProperty("success", out var success) ||
            success.ValueKind != JsonValueKind.True ||
            !response.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object ||
            !HasExactProperties(
                data,
                "schemaVersion", "writebackId", "commandId", "pharmacyId", "orderId",
                "candidateId", "pmsReferenceId", "proofRecordId", "proofDigest",
                "transition", "status", "resultCode", "completedAt", "idempotent") ||
            !data.TryGetProperty("schemaVersion", out var schema) ||
            schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version) || version != 2 ||
            !TryReadString(data, "writebackId", out var writebackId) ||
            !TryReadString(data, "commandId", out var commandId) ||
            !TryReadString(data, "pharmacyId", out var pharmacyId) ||
            !TryReadString(data, "orderId", out var orderId) ||
            !TryReadString(data, "candidateId", out var candidateId) ||
            !TryReadString(data, "pmsReferenceId", out var pmsReferenceId) ||
            !TryReadNullableString(data, "proofRecordId", out var proofRecordId) ||
            !TryReadNullableString(data, "proofDigest", out var proofDigest) ||
            !TryReadString(data, "transition", out var transition) ||
            !TryReadString(data, "status", out var status) ||
            !TryReadString(data, "resultCode", out var resultCodeRaw) ||
            !TryReadString(data, "completedAt", out var completedAtRaw) ||
            !data.TryGetProperty("idempotent", out var idempotent) ||
            idempotent.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !TryParseWritebackResult(resultCodeRaw, out var resultCode) ||
            !OffsetTimestamp.IsMatch(completedAtRaw) ||
            !DateTimeOffset.TryParse(
                completedAtRaw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var completedAt))
            return false;

        var expectedStatus = expectedResult switch
        {
            DeliveryWritebackResultCode.Success or DeliveryWritebackResultCode.AlreadyAtTarget => "succeeded",
            _ => "needs_attention",
        };
        if (!IsCanonicalUuid(writebackId) || !IsCanonicalUuid(commandId) ||
            !IsCanonicalUuid(pharmacyId) || !IsCanonicalUuid(orderId) ||
            !IsCanonicalUuid(candidateId) ||
            writebackId != command.WritebackId || commandId != command.CommandId ||
            pharmacyId != command.PharmacyId || orderId != command.OrderId ||
            candidateId != command.CandidateId ||
            pmsReferenceId != command.PmsReferenceId ||
            proofRecordId != command.ProofRecordId || proofDigest != command.ProofDigest ||
            transition != command.Transition ||
            resultCode != expectedResult || status != expectedStatus)
            return false;

        receipt = new DeliveryWritebackCallbackReceipt(
            writebackId,
            commandId,
            pharmacyId,
            orderId,
            candidateId,
            pmsReferenceId,
            proofRecordId,
            proofDigest,
            transition,
            status,
            resultCode,
            completedAt,
            idempotent.GetBoolean(),
            proof ?? new DeliveryWritebackSignedProof("", "", "", ""));
        return true;
    }

    public async Task<JsonElement?> PostSignedAsync(string path, object payload, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(payload);
        OutboundPhiGuard.AssertAllowed(path, body, _options);
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        _signer.ApplyHeaders(request, body);

        using var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct).ConfigureAwait(false);
        await EnsureCloudSuccessAsync(response, path, ct).ConfigureAwait(false);

        var responseBytes = await ReadResponseBytesBoundedAsync(
            response, MaxSignedResponseBytes, ct).ConfigureAwait(false);
        if (responseBytes is null) return null;
        try
        {
            return JsonSerializer.Deserialize<JsonElement>(responseBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(responseBytes);
        }
    }

    public async Task<JsonElement?> PostSignedVerifiedAsync(string path, object payload, string publicKeyDer, CancellationToken ct)
    {
        return await PostSignedVerifiedCoreAsync(
            path,
            payload,
            publicKeyDer,
            allowTypedPatientDetails: false,
            ct).ConfigureAwait(false);
    }

    private async Task<JsonElement?> PostSignedVerifiedCoreAsync(
        string path,
        object payload,
        string publicKeyDer,
        bool allowTypedPatientDetails,
        CancellationToken ct) =>
        await SendSignedVerifiedCoreAsync(
            HttpMethod.Post,
            path,
            payload,
            publicKeyDer,
            allowTypedPatientDetails,
            ct).ConfigureAwait(false);

    private async Task<JsonElement?> SendSignedVerifiedCoreAsync(
        HttpMethod method,
        string path,
        object payload,
        string publicKeyDer,
        bool allowTypedPatientDetails,
        CancellationToken ct) =>
        (await SendSignedVerifiedEnvelopeCoreAsync(
            method,
            path,
            payload,
            publicKeyDer,
            RemoteCommandTrust.CommandV1KeyId,
            allowTypedPatientDetails,
            ct).ConfigureAwait(false))?.Body;

    private async Task<VerifiedResponse?> SendSignedVerifiedEnvelopeCoreAsync(
        HttpMethod method,
        string path,
        object payload,
        string publicKeyDer,
        string keyId,
        bool allowTypedPatientDetails,
        CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(payload);
        if (!allowTypedPatientDetails)
            OutboundPhiGuard.AssertAllowed(path, body, _options);
        using var request = new HttpRequestMessage(method, path);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        _signer.ApplyHeaders(request, body);

        using var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct).ConfigureAwait(false);
        await EnsureCloudSuccessAsync(response, path, ct).ConfigureAwait(false);

        var responseBytes = await ReadResponseBytesBoundedAsync(
            response, MaxSignedResponseBytes, ct).ConfigureAwait(false);
        if (responseBytes is null) return null;

        try
        {
            // Reject response bodies that are not signed by the command control plane.
            if (!response.Headers.TryGetValues("X-Response-Signature", out var sigValues) ||
                sigValues.ToArray() is not [var responseSignature] ||
                !VerifyEcdsaSignature(responseBytes, responseSignature, publicKeyDer))
            {
                Serilog.Log.Warning(
                    "Cloud response ECDSA signature missing or invalid for {Path} — rejecting",
                    path);
                return null;
            }

            using var document = JsonDocument.Parse(
                responseBytes,
                new JsonDocumentOptions { MaxDepth = 16, CommentHandling = JsonCommentHandling.Disallow });
            var responseDigest = SHA256.HashData(responseBytes);
            try
            {
                return new VerifiedResponse(
                    document.RootElement.Clone(),
                    keyId,
                    responseSignature,
                    Convert.ToHexString(responseDigest).ToLowerInvariant(),
                    Encoding.UTF8.GetString(responseBytes));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(responseDigest);
            }
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(responseBytes);
        }
    }

    private static bool TryParseWritebackResult(
        string value,
        out DeliveryWritebackResultCode result)
    {
        result = value switch
        {
            "success" => DeliveryWritebackResultCode.Success,
            "already_at_target" => DeliveryWritebackResultCode.AlreadyAtTarget,
            "post_verify_mismatch" => DeliveryWritebackResultCode.PostVerifyMismatch,
            "status_conflict" => DeliveryWritebackResultCode.StatusConflict,
            "retry_exhausted" => DeliveryWritebackResultCode.RetryExhausted,
            "manual_review" => DeliveryWritebackResultCode.ManualReview,
            _ => default,
        };
        return value is "success" or "already_at_target" or "post_verify_mismatch" or
            "status_conflict" or "retry_exhausted" or "manual_review";
    }

    private static async Task EnsureCloudSuccessAsync(
        HttpResponseMessage response,
        string path,
        CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await ReadErrorResponseBodyBoundedAsync(response, ct)
            .ConfigureAwait(false);
        var safeReason = CloudErrorSanitizer.FromBody(body);
        throw CloudErrorResponse.Create(
            $"Cloud request {path} failed with {(int)response.StatusCode} ({response.ReasonPhrase}); reason={safeReason}",
            response.StatusCode,
            body);
    }

    private static async Task<string?> ReadErrorResponseBodyBoundedAsync(
        HttpResponseMessage response,
        CancellationToken ct)
    {
        var bytes = await ReadResponseBytesBoundedAsync(
            response, MaxErrorResponseBytes, ct).ConfigureAwait(false);
        if (bytes is null) return null;
        try
        {
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static async Task<byte[]?> ReadResponseBytesBoundedAsync(
        HttpResponseMessage response,
        int maximumBytes,
        CancellationToken ct)
    {
        var content = response.Content;
        if (content is null ||
            content.Headers.ContentLength > maximumBytes)
            return null;
        await using var stream = await content.ReadAsStreamAsync(ct)
            .ConfigureAwait(false);
        var buffer = new byte[maximumBytes + 1];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(count), ct)
                .ConfigureAwait(false);
            if (read == 0) break;
            count += read;
        }
        if (count is 0 || count > maximumBytes)
        {
            CryptographicOperations.ZeroMemory(buffer);
            return null;
        }
        var exact = buffer.AsSpan(0, count).ToArray();
        CryptographicOperations.ZeroMemory(buffer);
        return exact;
    }

    private static bool VerifyEcdsaSignature(
        ReadOnlySpan<byte> body,
        string signatureBase64,
        string publicKeyDer)
    {
        if (string.IsNullOrEmpty(signatureBase64)) return false;
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyDer), out _);
            var sigBytes = Convert.FromBase64String(signatureBase64);
            try
            {
                if (sigBytes.Length != 64) return false;
                return ecdsa.VerifyData(
                    body,
                    sigBytes,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(sigBytes);
            }
        }
        catch { return false; }
    }

    /// <summary>
    /// Acknowledges execution of a signed cloud command. Updates agent_commands row
    /// with status=executed or failed, plus optional result/error.
    /// </summary>
    public async Task AckCommandAsync(string commandId, bool success, object? result, string? error, CancellationToken ct)
    {
        _ = await TryAckCommandAsync(commandId, success, result, error, ct).ConfigureAwait(false);
    }

    internal async Task<bool> TryAckCommandAsync(
        string commandId,
        bool success,
        object? result,
        string? error,
        CancellationToken ct)
    {
        try
        {
            await PostSignedAsync(
                $"/api/agent/commands/{commandId}/ack",
                new
                {
                    status = success ? "executed" : "failed",
                    result,
                    error,
                },
                ct);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
            when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            // The strict ACK route uses conflict only after the command is
            // already terminal. Retrying cannot change that state, so a
            // durable outbox may safely converge without reopening execution.
            Serilog.Log.Information("core.command_ack_terminal_conflict");
            return true;
        }
        catch (Exception ex)
        {
            // Best-effort ack — don't crash the agent if cloud is unreachable.
            Serilog.Log.Warning(
                "core.command_ack_failed exception_type={ExceptionType}",
                ex.GetType().Name);
            return false;
        }
    }

    private static bool HasExactProperties(JsonElement element, params string[] expected)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Add(property.Name)) return false;
        }
        return names.SetEquals(expected);
    }

    private static bool TryReadString(JsonElement element, string propertyName, out string value)
    {
        value = "";
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
            return false;
        value = property.GetString() ?? "";
        return value.Length > 0 && value.Length <= 200 && !value.Any(char.IsControl);
    }

    private static bool TryReadNullableString(
        JsonElement element,
        string propertyName,
        out string? value)
    {
        value = null;
        if (!element.TryGetProperty(propertyName, out var property)) return false;
        if (property.ValueKind == JsonValueKind.Null) return true;
        if (property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString();
        return value is { Length: > 0 and <= 200 } && !value.Any(char.IsControl);
    }

    private static bool IsCanonicalUuid(string value) =>
        value.Length == 36 && Guid.TryParseExact(value, "D", out var parsed) &&
        string.Equals(parsed.ToString("D"), value, StringComparison.Ordinal);

    public async Task<SelfUninstallArchiveReceipt?> UploadAuditArchiveAsync(
        string archiveJson,
        string digest,
        CancellationToken ct)
    {
        var response = await PostSignedAsync("/api/agent/audit-archive",
            new { archive = archiveJson, archiveDigest = digest }, ct);
        if (response == null) return null;
        try
        {
            return JsonSerializer.Deserialize<SelfUninstallArchiveReceipt>(
                response.Value.GetRawText(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch { return null; }
    }

    public async Task<string?> UploadPomAsync(string pomJson, string digest, CancellationToken ct)
    {
        var response = await PostSignedAsync("/api/agent/pom", new { pom = pomJson, digest }, ct);
        if (response == null) return null;

        try
        {
            if (response.Value.TryGetProperty("pomId", out var id))
                return id.GetString();
        }
        catch { /* malformed response */ }

        return null;
    }

    public void Dispose() => _http.Dispose();
}
