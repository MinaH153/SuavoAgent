using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Tests.State;

internal static class DeliveryWritebackReceiptTestSigner
{
    internal const string KeyId = "test-writeback-receipt-v1";
    private static readonly object Gate = new();
    private static readonly ECDsa Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private static readonly IReadOnlyDictionary<string, string> Keys =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [KeyId] = Convert.ToBase64String(Key.ExportSubjectPublicKeyInfo()),
        };

    internal static IReadOnlyDictionary<string, string> TrustedKeys => Keys;

    internal static DeliveryWritebackCallbackReceipt Create(
        AgentDeliveryWritebackCommand command,
        DeliveryWritebackResultCode resultCode,
        DateTimeOffset completedAt,
        bool idempotent = false)
    {
        var status = resultCode switch
        {
            DeliveryWritebackResultCode.Success or DeliveryWritebackResultCode.AlreadyAtTarget => "succeeded",
            _ => "needs_attention",
        };
        var canonicalBody = JsonSerializer.Serialize(new
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
                status,
                resultCode = resultCode.ToWireValue(),
                completedAt,
                idempotent,
            },
        });
        var bytes = Encoding.UTF8.GetBytes(canonicalBody);
        byte[] signature;
        lock (Gate)
        {
            signature = Key.SignData(
                bytes,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }

        try
        {
            return new DeliveryWritebackCallbackReceipt(
                command.WritebackId,
                command.CommandId,
                command.PharmacyId,
                command.OrderId,
                command.CandidateId,
                command.PmsReferenceId,
                command.ProofRecordId,
                command.ProofDigest,
                command.Transition,
                status,
                resultCode,
                completedAt,
                idempotent,
                new DeliveryWritebackSignedProof(
                    KeyId,
                    Convert.ToBase64String(signature),
                    Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                    canonicalBody));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
