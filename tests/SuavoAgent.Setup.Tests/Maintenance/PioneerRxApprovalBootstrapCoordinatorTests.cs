using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Contracts.Security;
using SuavoAgent.Setup.Maintenance;
using SuavoAgent.Setup.Security;
using Xunit;

namespace SuavoAgent.Setup.Tests.Maintenance;

public sealed class PioneerRxApprovalBootstrapCoordinatorTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);
    private const string ReceiptId = "11111111-1111-4111-8111-111111111111";
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "pioneerrx-bootstrap-" + Guid.NewGuid().ToString("N"));

    public PioneerRxApprovalBootstrapCoordinatorTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Invalid_command_line_is_rejected_before_platform_or_privilege_checks()
    {
        Assert.Equal(2, PioneerRxApprovalBootstrapCoordinator.Run(Array.Empty<string>()));
        Assert.Equal(2, PioneerRxApprovalBootstrapCoordinator.Run(new[] { "--wrong" }));
    }

    [Theory]
    [InlineData((int)PioneerRxProposalStatus.Pending, "pending")]
    [InlineData((int)PioneerRxProposalStatus.SecurityReviewRequired, "security_review_required")]
    [InlineData((int)PioneerRxProposalStatus.Unknown, "submitting")]
    public void Submission_transition_persists_recoverable_state(
        int statusValue,
        string expected)
    {
        var status = (PioneerRxProposalStatus)statusValue;
        var current = State();
        var serverId = status == PioneerRxProposalStatus.Unknown
            ? null
            : "22222222-2222-4222-8222-222222222222";

        var transition = PioneerRxApprovalBootstrapCoordinator.TransitionSubmission(
            current,
            new PioneerRxProposalSubmission(status, serverId),
            Now);

        Assert.Equal(expected, transition.Outcome);
        Assert.NotNull(transition.NextState);
        Assert.Equal(expected, transition.NextState.Status);
        Assert.Equal(serverId ?? current.ProposalId, transition.NextState.ProposalId);
        Assert.Equal(Utc(Now.AddSeconds(30)), transition.NextState.NextPollAtUtc);
        Assert.Equal(Utc(Now), transition.NextState.UpdatedAtUtc);
        Assert.Same(current.Proposal, transition.NextState.Proposal);
    }

    [Fact]
    public void Rejected_submission_is_terminal_without_a_next_state()
    {
        var transition = PioneerRxApprovalBootstrapCoordinator.TransitionSubmission(
            State(),
            new PioneerRxProposalSubmission(PioneerRxProposalStatus.Rejected),
            Now);

        Assert.Equal("rejected", transition.Outcome);
        Assert.Null(transition.NextState);
    }

    [Theory]
    [InlineData((int)PioneerRxProposalStatus.Approved)]
    [InlineData((int)PioneerRxProposalStatus.Revoked)]
    public void Impossible_submission_status_is_rejected(int statusValue)
    {
        var status = (PioneerRxProposalStatus)statusValue;
        Assert.Throws<InvalidDataException>(() =>
            PioneerRxApprovalBootstrapCoordinator.TransitionSubmission(
                State(),
                new PioneerRxProposalSubmission(status),
                Now));
    }

    [Fact]
    public void Noncanonical_server_proposal_identity_is_rejected()
    {
        Assert.Throws<InvalidDataException>(() =>
            PioneerRxApprovalBootstrapCoordinator.TransitionSubmission(
                State(),
                new PioneerRxProposalSubmission(PioneerRxProposalStatus.Pending, "NOT-A-UUID"),
                Now));
    }

    [Fact]
    public void Bootstrap_request_is_bound_to_exact_consent_bytes_and_time_window()
    {
        var consentPath = Path.Combine(_root, "consent-receipt.json");
        var consent = Encoding.UTF8.GetBytes("{\"accepted\":true}");
        File.WriteAllBytes(consentPath, consent);
        var request = new PioneerRxApprovalBootstrapRequest(
            PioneerRxApprovalBootstrapContract.SchemaVersion,
            "S-1-5-21-1-2-3-1001",
            Sha(consent),
            Utc(Now));

        PioneerRxApprovalBootstrapCoordinator.ValidateBootstrapRequest(
            request,
            consentPath,
            Now);

        Assert.Throws<InvalidDataException>(() =>
            PioneerRxApprovalBootstrapCoordinator.ValidateBootstrapRequest(
                request with { ConsentReceiptSha256 = new string('0', 64) },
                consentPath,
                Now));
        Assert.Throws<InvalidDataException>(() =>
            PioneerRxApprovalBootstrapCoordinator.ValidateBootstrapRequest(
                request with { RequestedAtUtc = Utc(Now.AddDays(-31)) },
                consentPath,
                Now));
        Assert.Throws<InvalidDataException>(() =>
            PioneerRxApprovalBootstrapCoordinator.ValidateBootstrapRequest(
                request with { RequestedAtUtc = Utc(Now.AddMinutes(6)) },
                consentPath,
                Now));
        Assert.Throws<InvalidDataException>(() =>
            PioneerRxApprovalBootstrapCoordinator.ValidateBootstrapRequest(
                request with { ApprovedBySid = "administrator" },
                consentPath,
                Now));
    }

    [Fact]
    public void Sql_certificate_digest_requires_unique_lowercase_hex()
    {
        var path = Path.Combine(_root, "appsettings.json");
        File.WriteAllText(path, $$"""
            { "Agent": { "SqlServerCertificateSha256": "{{new string('a', 64)}}" } }
            """);

        Assert.Equal(
            new string('a', 64),
            PioneerRxApprovalBootstrapCoordinator.ReadSqlCertificateDigest(path));

        File.WriteAllText(path, $$"""
            { "Agent": {
                "SqlServerCertificateSha256": "{{new string('a', 64)}}",
                "sqlservercertificatesha256": "{{new string('b', 64)}}"
            } }
            """);
        Assert.Throws<InvalidDataException>(() =>
            PioneerRxApprovalBootstrapCoordinator.ReadSqlCertificateDigest(path));

        File.WriteAllText(path, $$"""
            { "Agent": { "SqlServerCertificateSha256": "{{new string('A', 64)}}" } }
            """);
        Assert.Throws<InvalidDataException>(() =>
            PioneerRxApprovalBootstrapCoordinator.ReadSqlCertificateDigest(path));
    }

    [Fact]
    public void Strict_reader_rejects_nested_case_insensitive_duplicate_properties()
    {
        var path = Path.Combine(_root, "request.json");
        File.WriteAllText(path, """
            {
              "schemaVersion": 1,
              "approvedBySid": "S-1-5-18",
              "consentReceiptSha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "requestedAtUtc": "2026-07-11T12:00:00.0000000Z",
              "REQUESTEDATUTC": "2026-07-11T12:00:00.0000000Z"
            }
            """);

        Assert.Throws<InvalidDataException>(() =>
            PioneerRxApprovalBootstrapCoordinator
                .ReadStrict<PioneerRxApprovalBootstrapRequest>(path));
    }

    [Fact]
    public void Bounded_utf8_reader_rejects_invalid_utf8_and_oversize()
    {
        var path = Path.Combine(_root, "bounded.txt");
        File.WriteAllBytes(path, new byte[] { 0xC3, 0x28 });
        Assert.Throws<DecoderFallbackException>(() =>
            PioneerRxApprovalBootstrapCoordinator.ReadBoundedUtf8(path, 16));

        File.WriteAllBytes(path, new byte[17]);
        Assert.Throws<InvalidDataException>(() =>
            PioneerRxApprovalBootstrapCoordinator.ReadBoundedUtf8(path, 16));
    }

    [Fact]
    public void Delete_regular_removes_only_regular_files()
    {
        var file = Path.Combine(_root, "state.json");
        File.WriteAllText(file, "state");
        PioneerRxApprovalBootstrapCoordinator.DeleteRegular(file);
        Assert.False(File.Exists(file));

        var directory = Path.Combine(_root, "directory");
        Directory.CreateDirectory(directory);
        Assert.Throws<InvalidDataException>(() =>
            PioneerRxApprovalBootstrapCoordinator.DeleteRegular(directory));
        PioneerRxApprovalBootstrapCoordinator.DeleteRegular(
            Path.Combine(_root, "missing.json"));
    }

    [Theory]
    [InlineData("11111111-1111-4111-8111-111111111111", true)]
    [InlineData("11111111-1111-4111-8111-11111111111A", false)]
    [InlineData("not-a-uuid", false)]
    public void Canonical_uuid_is_exact(string value, bool expected) =>
        Assert.Equal(expected, PioneerRxApprovalBootstrapCoordinator.CanonicalUuid(value));

    [Theory]
    [InlineData("S-1-5-18", true)]
    [InlineData("S-1-5-21-1-2-3-1001", true)]
    [InlineData("s-1-5-18", false)]
    [InlineData("S-1-a-18", false)]
    [InlineData("S-1-", false)]
    public void Sid_parser_is_strict(string value, bool expected) =>
        Assert.Equal(expected, PioneerRxApprovalBootstrapCoordinator.IsSid(value));

    [Fact]
    public void Fixed_hash_comparison_rejects_shape_and_case_changes()
    {
        var hash = new string('a', 64);
        Assert.True(PioneerRxApprovalBootstrapCoordinator.LowerHex64(hash));
        Assert.True(PioneerRxApprovalBootstrapCoordinator.FixedHexEquals(hash, hash));
        Assert.False(PioneerRxApprovalBootstrapCoordinator.FixedHexEquals(
            hash,
            new string('A', 64)));
        Assert.False(PioneerRxApprovalBootstrapCoordinator.FixedHexEquals(hash, "short"));
    }

    private static PioneerRxApprovalBootstrapState State() => new(
        PioneerRxApprovalBootstrapContract.SchemaVersion,
        ReceiptId,
        Proposal(),
        new string('a', 64),
        "submitting",
        Utc(Now),
        Utc(Now));

    private static PioneerRxProcessApprovalReceipt Proposal() => new(
        PioneerRxProcessApprovalContract.CurrentSchemaVersion,
        ReceiptId,
        "22222222-2222-4222-8222-222222222222",
        "33333333-3333-4333-8333-333333333333",
        new string('a', 64),
        "spki",
        "PioneerPharmacy.exe",
        @"C:\Program Files\PioneerRx\PioneerPharmacy.exe",
        new string('b', 64),
        "CN=New Tech Computer Systems",
        new string('c', 64),
        "PioneerRx",
        "1.2.3.4",
        "44444444-4444-4444-8444-444444444444",
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

    private static string Utc(DateTimeOffset value) =>
        PioneerRxApprovalBootstrapCoordinator.Utc(value);

    private static string Sha(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
