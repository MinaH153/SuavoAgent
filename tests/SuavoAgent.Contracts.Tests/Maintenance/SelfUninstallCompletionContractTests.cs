using System.Security.Cryptography;
using System.Text;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Contracts.Security;
using Xunit;

namespace SuavoAgent.Contracts.Tests.Maintenance;

public sealed class SelfUninstallCompletionContractTests
{
    private const string Fingerprint = "machine-123";
    private const string AgentId = "11111111-1111-4111-8111-111111111111";
    private const string PharmacyId = "22222222-2222-4222-8222-222222222222";
    private const string CommandId = "33333333-3333-4333-8333-333333333333";
    private const string Nonce = "44444444-4444-4444-8444-444444444444";
    private const string ArchiveId = "55555555-5555-4555-8555-555555555555";
    private static readonly DateTimeOffset CompletedAt =
        DateTimeOffset.Parse("2026-07-11T20:30:40.1234567Z");

    [Fact]
    public void Cleanup_evidence_canonical_and_digest_are_exact_and_deterministic()
    {
        var evidence = Evidence();

        var canonical = SelfUninstallCompletionContract.BuildCleanupEvidenceCanonical(evidence);

        Assert.Equal(
            "suavo.self-uninstall-cleanup-evidence.v1|1|retained_evidence_only|" +
            "suavo-native-maintenance|3.77.0|true|true|true|true|true|true|true|true|0",
            canonical);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
                .ToLowerInvariant(),
            SelfUninstallCompletionContract.ComputeCleanupEvidenceDigest(evidence));
    }

    [Fact]
    public void Signed_ticket_uses_exact_root_canonical_and_validates_p1363_base64url()
    {
        using var keys = ActiveKeys();
        using var key = keys.OpenExistingForMaintenance(Fingerprint);
        var envelope = SelfUninstallCompletionContract.CreateSignedEnvelope(
            Request(),
            PharmacyId,
            Evidence(),
            key.Enrollment.KeyId,
            bytes => key.Sign(bytes.Span),
            CompletedAt);

        Assert.Equal(
            $"suavo.self-uninstall-completion.v1|1|{CommandId}|{AgentId}|{PharmacyId}|" +
            $"machine-123|{Nonce}|{ArchiveId}|" + Request().ArchiveDigest + "|" +
            "2026-07-11T20:29:00.000Z|" +
            envelope.Ticket.CleanupEvidenceDigest + "|" +
            "2026-07-11T20:30:40.1234567Z|" + key.Enrollment.KeyId,
            SelfUninstallCompletionContract.BuildTicketCanonical(envelope.Ticket));
        Assert.DoesNotContain('=', envelope.Ticket.Signature);
        Assert.Equal(86, envelope.Ticket.Signature.Length);

        var result = SelfUninstallCompletionContract.Validate(
            envelope,
            Request(),
            PharmacyId,
            key.Enrollment.KeyId,
            key.Enrollment.PublicKeySpki);

        Assert.True(result.IsValid, result.Code);
    }

    [Fact]
    public void Any_evidence_or_ticket_tamper_is_rejected()
    {
        using var keys = ActiveKeys();
        using var key = keys.OpenExistingForMaintenance(Fingerprint);
        var request = Request();
        var envelope = SelfUninstallCompletionContract.CreateSignedEnvelope(
            request,
            PharmacyId,
            Evidence(),
            key.Enrollment.KeyId,
            bytes => key.Sign(bytes.Span),
            CompletedAt);

        var evidenceTamper = envelope with
        {
            CleanupEvidence = envelope.CleanupEvidence with { ArpRegistrationAbsent = false },
        };
        var ticketTamper = envelope with
        {
            Ticket = envelope.Ticket with { AgentId = "agent-2" },
        };

        Assert.Equal(
            "cleanup_not_terminal",
            SelfUninstallCompletionContract.Validate(
                evidenceTamper, request, PharmacyId, key.Enrollment.KeyId,
                key.Enrollment.PublicKeySpki).Code);
        Assert.Equal(
            "completion_request_binding_mismatch",
            SelfUninstallCompletionContract.Validate(
                ticketTamper, request, PharmacyId, key.Enrollment.KeyId,
            key.Enrollment.PublicKeySpki).Code);
    }

    [Fact]
    public void Noncanonical_base64url_signature_alias_is_rejected()
    {
        using var keys = ActiveKeys();
        using var key = keys.OpenExistingForMaintenance(Fingerprint);
        var request = Request();
        var envelope = SelfUninstallCompletionContract.CreateSignedEnvelope(
            request,
            PharmacyId,
            Evidence(),
            key.Enrollment.KeyId,
            bytes => key.Sign(bytes.Span),
            CompletedAt);
        const string alphabet =
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        var lastIndex = alphabet.IndexOf(envelope.Ticket.Signature[^1]);
        Assert.True(lastIndex is 0 or 16 or 32 or 48);
        var alias = envelope.Ticket.Signature[..^1] + alphabet[lastIndex + 1];

        var result = SelfUninstallCompletionContract.Validate(
            envelope with { Ticket = envelope.Ticket with { Signature = alias } },
            request,
            PharmacyId,
            key.Enrollment.KeyId,
            key.Enrollment.PublicKeySpki);

        Assert.False(result.IsValid);
        Assert.Equal("completion_signature_invalid", result.Code);
    }

    [Fact]
    public void Evidence_residue_count_must_exactly_equal_false_predicates_and_zero()
    {
        var inconsistent = Evidence() with { ResidueCount = 1 };
        var truthfulResidue = Evidence() with
        {
            RuntimeDirectoryAbsent = false,
            ResidueCount = 1,
        };

        Assert.Equal(
            "cleanup_not_terminal",
            SelfUninstallCompletionContract.ValidateCleanupEvidence(inconsistent).Code);
        Assert.Equal(
            "cleanup_not_terminal",
            SelfUninstallCompletionContract.ValidateCleanupEvidence(truthfulResidue).Code);
    }

    [Fact]
    public void Envelope_parser_rejects_extra_fields()
    {
        using var keys = ActiveKeys();
        using var key = keys.OpenExistingForMaintenance(Fingerprint);
        var envelope = SelfUninstallCompletionContract.CreateSignedEnvelope(
            Request(), PharmacyId, Evidence(), key.Enrollment.KeyId,
            bytes => key.Sign(bytes.Span), CompletedAt);
        var json = SelfUninstallCompletionContract.Serialize(envelope);
        json = json[..^1] + ",\"unexpected\":true}";

        Assert.False(SelfUninstallCompletionContract.TryDeserialize(
            json, out _, out var code));
        Assert.Equal("completion_invalid_json", code);
    }

    [Fact]
    public void Finalize_response_requires_exact_server_receipt_digest()
    {
        using var keys = ActiveKeys();
        using var key = keys.OpenExistingForMaintenance(Fingerprint);
        var envelope = SelfUninstallCompletionContract.CreateSignedEnvelope(
            Request(), PharmacyId, Evidence(), key.Enrollment.KeyId,
            bytes => key.Sign(bytes.Span), CompletedAt);
        var expected = SelfUninstallCompletionContract.ComputeReceiptDigest(envelope.Ticket);
        var response = $$"""{"status":"finalized","commandId":"{{CommandId}}","receiptDigest":"{{expected}}"}""";

        Assert.True(SelfUninstallCompletionContract.IsExactFinalizedResponse(
            response, envelope, out var receipt));
        Assert.Equal(expected, receipt);

        var wrong = new string('0', 64);
        var wrongResponse = $$"""{"status":"finalized","commandId":"{{CommandId}}","receiptDigest":"{{wrong}}"}""";
        Assert.False(SelfUninstallCompletionContract.IsExactFinalizedResponse(
            wrongResponse, envelope, out _));
    }

    [Fact]
    public void Destroy_for_uninstall_requires_exact_active_key_and_removes_all_versions()
    {
        using var keys = ActiveKeys();
        using var active = keys.OpenExisting(Fingerprint);
        var keyId = active.Enrollment.KeyId;

        Assert.Throws<InvalidOperationException>(() =>
            keys.DestroyForUninstall(Fingerprint, new string('0', 64)));

        keys.DestroyForUninstall(Fingerprint, keyId);

        Assert.Throws<InvalidOperationException>(() => keys.OpenExisting(Fingerprint));
    }

    private static InMemoryDeviceAttestationKeyProvider ActiveKeys()
    {
        var provider = new InMemoryDeviceAttestationKeyProvider();
        using var pending = provider.OpenOrCreate(Fingerprint);
        provider.CommitPending(Fingerprint, pending.Enrollment.KeyId);
        return provider;
    }

    private static SelfUninstallCleanupEvidence Evidence() =>
        SelfUninstallCompletionContract.CreateCleanupEvidence(
            "3.77.0",
            servicesAbsent: true,
            scheduledUninstallTaskAbsent: true,
            protocolRegistrationAbsent: true,
            arpRegistrationAbsent: true,
            installDirectoryAbsent: true,
            runtimeDirectoryAbsent: true,
            retainedEvidencePresent: true,
            operationalCredentialsAbsent: true);

    private static SelfUninstallRequest Request()
    {
        var digest = RemoteCommandTrust.ComputeSha256Hex("archive");
        return new(
            SelfUninstallContract.SchemaVersion,
            SelfUninstallContract.CommandName,
            AgentId,
            Fingerprint,
            "2026-07-11T20:29:00.0000000Z",
            Nonce,
            "command-key",
            "command-signature",
            $"{{\"commandId\":\"{CommandId}\"}}",
            RemoteCommandTrust.ComputeSha256Hex(
                $"{{\"commandId\":\"{CommandId}\"}}"),
            CommandId,
            "2026-07-11T20:29:00.0000000Z",
            digest,
            new SelfUninstallArchiveReceipt(
                ArchiveId,
                digest,
                "2026-07-11T20:29:00.000Z",
                Nonce,
                "receipt-key",
                "receipt-signature"));
    }
}
