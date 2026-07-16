using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SuavoAgent.Core.State;

internal sealed record AgentDeliveryWritebackCommand(
    int SchemaVersion,
    string WritebackId,
    string CandidateId,
    string RxHash,
    string EvidenceId,
    string PharmacyId,
    string OrderId,
    string InboxItemId,
    string PmsReferenceId,
    string? ProofRecordId,
    string? ProofDigest,
    string Transition,
    string TransitionAt,
    string CommandId);

internal enum DeliveryWritebackResultCode
{
    Success,
    AlreadyAtTarget,
    PostVerifyMismatch,
    StatusConflict,
    RetryExhausted,
    ManualReview,
}

internal static class DeliveryWritebackResultCodeExtensions
{
    internal static string ToWireValue(this DeliveryWritebackResultCode result) => result switch
    {
        DeliveryWritebackResultCode.Success => "success",
        DeliveryWritebackResultCode.AlreadyAtTarget => "already_at_target",
        DeliveryWritebackResultCode.PostVerifyMismatch => "post_verify_mismatch",
        DeliveryWritebackResultCode.StatusConflict => "status_conflict",
        DeliveryWritebackResultCode.RetryExhausted => "retry_exhausted",
        DeliveryWritebackResultCode.ManualReview => "manual_review",
        _ => throw new ArgumentOutOfRangeException(nameof(result)),
    };

    internal static bool IsCloudSuccess(this DeliveryWritebackResultCode result) =>
        result is DeliveryWritebackResultCode.Success or DeliveryWritebackResultCode.AlreadyAtTarget;
}

internal enum DeliveryWritebackLedgerState
{
    Registered,
    Executing,
    ResultPendingCallback,
    ReceiptVerified,
    Acked,
}

internal sealed record DeliveryWritebackCallbackReceipt(
    string WritebackId,
    string CommandId,
    string PharmacyId,
    string OrderId,
    string CandidateId,
    string PmsReferenceId,
    string? ProofRecordId,
    string? ProofDigest,
    string Transition,
    string Status,
    DeliveryWritebackResultCode ResultCode,
    DateTimeOffset CompletedAt,
    bool Idempotent,
    DeliveryWritebackSignedProof Proof);

internal sealed record DeliveryWritebackSignedProof(
    string KeyId,
    string SignatureBase64,
    string CanonicalBodySha256,
    string CanonicalBodyJson)
{
    private const int MaxCanonicalBodyBytes = 16 * 1024;

    internal bool Verify(
        DeliveryWritebackCallbackReceipt receipt,
        IReadOnlyDictionary<string, string> trustedKeys)
    {
        if (string.IsNullOrWhiteSpace(KeyId) || KeyId.Length > 80 ||
            !trustedKeys.TryGetValue(KeyId, out var publicKeyDer) ||
            CanonicalBodySha256.Length != 64 ||
            CanonicalBodySha256.Any(ch => ch is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) ||
            string.IsNullOrEmpty(CanonicalBodyJson))
            return false;

        byte[] bodyBytes = Encoding.UTF8.GetBytes(CanonicalBodyJson);
        byte[]? signature = null;
        byte[]? expectedDigest = null;
        byte[]? actualDigest = null;
        try
        {
            if (bodyBytes.Length is <= 0 or > MaxCanonicalBodyBytes) return false;
            using var document = JsonDocument.Parse(
                bodyBytes,
                new JsonDocumentOptions { MaxDepth = 8, CommentHandling = JsonCommentHandling.Disallow });
            expectedDigest = Convert.FromHexString(CanonicalBodySha256);
            actualDigest = SHA256.HashData(bodyBytes);
            if (!CryptographicOperations.FixedTimeEquals(expectedDigest, actualDigest)) return false;

            signature = Convert.FromBase64String(SignatureBase64);
            if (signature.Length != 64) return false;
            using var verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyDer), out var consumed);
            if (consumed <= 0 || !verifier.VerifyHash(
                    actualDigest,
                    signature,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                return false;

            return ExactReceiptBody(document.RootElement, receipt);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bodyBytes);
            if (signature is not null) CryptographicOperations.ZeroMemory(signature);
            if (expectedDigest is not null) CryptographicOperations.ZeroMemory(expectedDigest);
            if (actualDigest is not null) CryptographicOperations.ZeroMemory(actualDigest);
        }
    }

    private static bool ExactReceiptBody(
        JsonElement root,
        DeliveryWritebackCallbackReceipt receipt)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !HasExactProperties(root, "success", "data") ||
            !root.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True ||
            !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
            !HasExactProperties(
                data,
                "schemaVersion", "writebackId", "commandId", "pharmacyId", "orderId",
                "candidateId", "pmsReferenceId", "proofRecordId", "proofDigest",
                "transition", "status", "resultCode", "completedAt", "idempotent") ||
            !data.TryGetProperty("schemaVersion", out var schema) ||
            schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version) || version != 2 ||
            !TryGetExactString(data, "writebackId", receipt.WritebackId) ||
            !TryGetExactString(data, "commandId", receipt.CommandId) ||
            !TryGetExactString(data, "pharmacyId", receipt.PharmacyId) ||
            !TryGetExactString(data, "orderId", receipt.OrderId) ||
            !TryGetExactString(data, "candidateId", receipt.CandidateId) ||
            !TryGetExactString(data, "pmsReferenceId", receipt.PmsReferenceId) ||
            !TryGetExactNullableString(data, "proofRecordId", receipt.ProofRecordId) ||
            !TryGetExactNullableString(data, "proofDigest", receipt.ProofDigest) ||
            !TryGetExactString(data, "transition", receipt.Transition) ||
            !TryGetExactString(data, "status", receipt.Status) ||
            !TryGetExactString(data, "resultCode", receipt.ResultCode.ToWireValue()) ||
            !data.TryGetProperty("completedAt", out var completedAtElement) ||
            completedAtElement.ValueKind != JsonValueKind.String ||
            !DateTimeOffset.TryParse(
                completedAtElement.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var completedAt) ||
            completedAt != receipt.CompletedAt ||
            !data.TryGetProperty("idempotent", out var idempotent) ||
            idempotent.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            idempotent.GetBoolean() != receipt.Idempotent)
            return false;
        return true;
    }

    private static bool HasExactProperties(JsonElement element, params string[] expected)
    {
        var actual = element.EnumerateObject().Select(property => property.Name).ToArray();
        return actual.Length == expected.Length &&
               actual.ToHashSet(StringComparer.Ordinal).SetEquals(expected);
    }

    private static bool TryGetExactString(JsonElement element, string name, string expected) =>
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        string.Equals(value.GetString(), expected, StringComparison.Ordinal);

    private static bool TryGetExactNullableString(
        JsonElement element,
        string name,
        string? expected) =>
        element.TryGetProperty(name, out var value) &&
        (expected is null
            ? value.ValueKind == JsonValueKind.Null
            : value.ValueKind == JsonValueKind.String &&
              string.Equals(value.GetString(), expected, StringComparison.Ordinal));
}

internal sealed record DeliveryWritebackLedgerItem(
    AgentDeliveryWritebackCommand Command,
    DeliveryWritebackLedgerState State,
    DeliveryWritebackResultCode? ResultCode,
    bool CorrelationBound,
    int ExecutionAttempts,
    int CallbackAttempts,
    DateTimeOffset? NextRetryAt,
    string? LastErrorCode,
    DeliveryWritebackCallbackReceipt? Receipt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

internal enum DeliveryWritebackLedgerRegistrationCode
{
    Registered,
    Idempotent,
    CommandConflict,
    WritebackConflict,
    Full,
}

internal sealed record DeliveryWritebackLedgerRegistrationResult(
    DeliveryWritebackLedgerRegistrationCode Code,
    DeliveryWritebackLedgerItem? Item)
{
    internal bool Accepted =>
        Code is DeliveryWritebackLedgerRegistrationCode.Registered or
            DeliveryWritebackLedgerRegistrationCode.Idempotent;
}

internal interface IDeliveryWritebackLedger
{
    DeliveryWritebackLedgerRegistrationResult Register(AgentDeliveryWritebackCommand command);
    IReadOnlyList<DeliveryWritebackLedgerItem> GetDue(
        string pharmacyId,
        int maxCount,
        DateTimeOffset now);
    DeliveryWritebackLedgerItem? Get(string commandId);
    DeliveryWritebackLedgerItem MarkExecuting(string commandId);
    DeliveryWritebackLedgerItem MarkCorrelationBound(string commandId);
    DeliveryWritebackLedgerItem RecordResult(
        string commandId,
        DeliveryWritebackResultCode resultCode);
    DeliveryWritebackLedgerItem Defer(
        string commandId,
        string errorCode,
        DateTimeOffset nextRetryAt,
        bool callbackAttempt);
    DeliveryWritebackLedgerItem MarkReceiptVerified(
        string commandId,
        DeliveryWritebackCallbackReceipt receipt);
    DeliveryWritebackLedgerItem MarkAcked(string commandId);
}

internal enum WritebackCorrelationRegistrationCode
{
    Registered,
    Idempotent,
    CorrelationNotFound,
    IdentityMismatch,
    CandidateMismatch,
    PatientRetrievalIncomplete,
    CommandReplayConflict,
    TransitionConflict,
    RawLookupUnavailable,
    SourceUnsupported,
}

internal sealed record WritebackCorrelationRegistrationResult(
    WritebackCorrelationRegistrationCode Code)
{
    internal bool Accepted =>
        Code is WritebackCorrelationRegistrationCode.Registered or
            WritebackCorrelationRegistrationCode.Idempotent;
}
