using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.State;
using SuavoAgent.Core.Tests.Pricing;
using Xunit;

namespace SuavoAgent.Core.Tests.Cloud;

public sealed partial class PricingJobCloudUploaderTests
{
    [Fact]
    public async Task WinningSend_CommitsBeforeCloudRevocation_ThenAllNewWorkStops()
    {
        var now = new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
        var signer = new BlockingSuccessSigner();
        var clock = new AuthorityTimeProvider(now.AddMinutes(1));
        var uploader = CreateUploader(signer, _db, clock);
        var (spec, _) = StageAuthorizedCompletedPayload(
            uploader,
            "send-revocation-linearization",
            now,
            now.AddDays(7));

        var flush = uploader.FlushPendingAsync(
            CancellationToken.None,
            includeDeferred: true);
        await signer.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        var revocation = Task.Run(() =>
            _db.LatchPricingCloudAuthorityRevocation(now.AddMinutes(2)));

        try
        {
            var admissionCode = await WaitForAuthorityDenialAsync(
                spec,
                clock,
                TimeSpan.FromSeconds(5));
            Assert.Equal("pricing_cloud_authority_revoked", admissionCode);

            var published = false;
            Assert.False(_db.TryPublishPricingArtifact(
                spec.JobId,
                spec.ApprovalId,
                spec.GrantDigest,
                clock,
                PricingTestAuthority.TrustedPublicKeys,
                () => published = true,
                out var publicationCode));
            Assert.Equal("pricing_cloud_authority_revoked", publicationCode);
            Assert.False(published);
        }
        finally
        {
            signer.Release();
        }

        await flush.WaitAsync(TimeSpan.FromSeconds(5));
        await revocation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(
            "accepted",
            _db.GetPricingResultOutbox(spec.JobId)!.State);
        Assert.False(_db.TryAdmitPricingJobAuthority(
            spec.JobId,
            spec.ApprovalId,
            spec.GrantDigest,
            clock.GetUtcNow(),
            PricingTestAuthority.TrustedPublicKeys,
            out var finalCode));
        Assert.Equal("pricing_cloud_authority_revoked", finalCode);
    }

    [Fact]
    public async Task LostResponse_AfterPicRevocation_UsesOnlyHashReceiptRecovery()
    {
        var now = new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
        var signer = new SequencedRecoverySigner(unsignedResponses: 1);
        var clock = new AuthorityTimeProvider(now.AddMinutes(1));
        var uploader = CreateUploader(signer, _db, clock);
        var (spec, grant) = StageAuthorizedCompletedPayload(
            uploader,
            "lost-response-exact-recovery",
            now,
            now.AddDays(7));

        await uploader.FlushPendingAsync(
            CancellationToken.None,
            includeDeferred: true);
        Assert.Single(signer.Paths);
        Assert.EndsWith("/results", signer.Paths[0], StringComparison.Ordinal);

        var revoked = PricingTestAuthority.InstallRevocation(
            _db,
            PricingTestAuthority.Revocation(grant, now.AddMinutes(2)),
            now.AddMinutes(2));
        Assert.True(revoked.Succeeded, revoked.Code);

        await uploader.FlushPendingAsync(
            CancellationToken.None,
            includeDeferred: true);

        Assert.Equal(2, signer.Paths.Count);
        Assert.EndsWith(
            "/results/receipt-recovery",
            signer.Paths[1],
            StringComparison.Ordinal);
        var recovery = signer.Payloads[1];
        var names = recovery.EnumerateObject()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.True(names.SetEquals(
            ["commandId", "approvalId", "grantDigest", "payloadSha256"]));
        Assert.Equal(4, names.Count);
        Assert.Equal(spec.ApprovalId, recovery.GetProperty("approvalId").GetString());
        Assert.Equal(spec.GrantDigest, recovery.GetProperty("grantDigest").GetString());
        Assert.Equal(
            _db.GetPricingResultOutbox(spec.JobId)!.PayloadSha256,
            recovery.GetProperty("payloadSha256").GetString());
        Assert.DoesNotContain(
            "items",
            recovery.GetRawText(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal("accepted", _db.GetPricingResultOutbox(spec.JobId)!.State);

        var invoked = false;
        var unrelated = await _db.ExecuteUnderPricingAuthorityAsync(
            spec.JobId,
            new string('f', 64),
            spec.ApprovalId!,
            spec.GrantDigest!,
            clock,
            PricingTestAuthority.TrustedPublicKeys,
            (_, _) =>
            {
                invoked = true;
                return Task.FromResult(true);
            },
            CancellationToken.None);
        Assert.False(unrelated.Admitted);
        Assert.Equal("pricing_cost_basis_approval_revoked", unrelated.Code);
        Assert.False(invoked);
    }

    [Fact]
    public async Task ConcurrentPendingReaders_SendOnce_AndReuseFirstDurableReceipt()
    {
        var now = new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
        var signer = new BlockingSuccessSigner();
        var uploader = CreateUploader(
            signer,
            _db,
            new AuthorityTimeProvider(now.AddMinutes(1)));
        var (spec, _) = StageAuthorizedCompletedPayload(
            uploader,
            "concurrent-pending-send-once",
            now,
            now.AddDays(7));

        var first = uploader.FlushPendingAsync(
            CancellationToken.None,
            includeDeferred: true);
        await signer.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        var second = uploader.FlushPendingAsync(
            CancellationToken.None,
            includeDeferred: true);
        signer.Release();

        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, signer.CallCount);
        var accepted = Assert.IsType<AgentStateDb.PricingResultOutboxEntry>(
            _db.GetPricingResultOutbox(spec.JobId));
        Assert.Equal("accepted", accepted.State);
        using var receipt = JsonDocument.Parse(accepted.AcceptedReceiptJson!);
        Assert.False(receipt.RootElement.GetProperty("idempotent").GetBoolean());
        Assert.Equal(0, accepted.AttemptCount);
    }

    [Fact]
    public async Task OverlappingStaleAndValidRevocations_BlockQueuedNewSend()
    {
        var now = new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
        var clock = new AuthorityTimeProvider(now.AddMinutes(1));
        var uploader = CreateUploader(new RecordingPostSigner(), _db, clock);
        var (spec, grant) = StageAuthorizedCompletedPayload(
            uploader,
            "overlapping-revocation-refcount",
            now,
            now.AddDays(7));
        var outbox = Assert.IsType<AgentStateDb.PricingResultOutboxEntry>(
            _db.GetPricingResultOutbox(spec.JobId));
        var blockerEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBlocker = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = _db.ExecuteUnderPricingAuthorityAsync(
            spec.JobId,
            outbox.PayloadSha256,
            spec.ApprovalId!,
            spec.GrantDigest!,
            clock,
            PricingTestAuthority.TrustedPublicKeys,
            async (_, ct) =>
            {
                blockerEntered.TrySetResult(true);
                await releaseBlocker.Task.WaitAsync(ct);
                return true;
            },
            CancellationToken.None);
        await blockerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var staleGrant = grant with
        {
            ProposalId = Guid.NewGuid().ToString("D"),
        };
        var stale = Task.Run(() => PricingTestAuthority.InstallRevocation(
            _db,
            PricingTestAuthority.Revocation(staleGrant, now.AddMinutes(2)),
            now.AddMinutes(2)));
        await WaitForRevocationCountAsync(
            spec.ApprovalId!,
            expected: 1,
            TimeSpan.FromSeconds(5));

        var invoked = false;
        var queuedSend = _db.ExecuteUnderPricingAuthorityAsync(
            spec.JobId,
            new string('e', 64),
            spec.ApprovalId!,
            spec.GrantDigest!,
            clock,
            PricingTestAuthority.TrustedPublicKeys,
            (_, _) =>
            {
                invoked = true;
                return Task.FromResult(true);
            },
            CancellationToken.None);
        var valid = Task.Run(() => PricingTestAuthority.InstallRevocation(
            _db,
            PricingTestAuthority.Revocation(grant, now.AddMinutes(2)),
            now.AddMinutes(2)));
        await WaitForRevocationCountAsync(
            spec.ApprovalId!,
            expected: 2,
            TimeSpan.FromSeconds(5));
        releaseBlocker.TrySetResult(true);

        await blocker.WaitAsync(TimeSpan.FromSeconds(5));
        var staleResult = await stale.WaitAsync(TimeSpan.FromSeconds(5));
        var validResult = await valid.WaitAsync(TimeSpan.FromSeconds(5));
        var sendResult = await queuedSend.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(
            AgentStateDb.PricingApprovalLedgerKind.Rejected,
            staleResult.Kind);
        Assert.True(validResult.Succeeded, validResult.Code);
        Assert.False(sendResult.Admitted);
        Assert.Equal("pricing_cost_basis_approval_revoked", sendResult.Code);
        Assert.False(invoked);
        Assert.Equal(
            0,
            _db.GetPendingPricingApprovalRevocationCount(spec.ApprovalId!));
    }

    [Fact]
    public async Task PersistentUnsignedRecovery_IsBoundedThenRequiresManualReconciliation()
    {
        var now = new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
        var signer = new SequencedRecoverySigner(unsignedResponses: int.MaxValue);
        var clock = new AuthorityTimeProvider(now.AddMinutes(1));
        var uploader = CreateUploader(signer, _db, clock);
        var (spec, grant) = StageAuthorizedCompletedPayload(
            uploader,
            "bounded-receipt-recovery",
            now,
            now.AddDays(7));

        await uploader.FlushPendingAsync(
            CancellationToken.None,
            includeDeferred: true);
        var revoked = PricingTestAuthority.InstallRevocation(
            _db,
            PricingTestAuthority.Revocation(grant, now.AddMinutes(2)),
            now.AddMinutes(2));
        Assert.True(revoked.Succeeded, revoked.Code);

        for (var attempt = 0; attempt < 3; attempt++)
            await uploader.FlushPendingAsync(
                CancellationToken.None,
                includeDeferred: true);

        Assert.Equal(4, signer.Paths.Count);
        Assert.All(
            signer.Paths.Skip(1),
            path => Assert.EndsWith(
                "/results/receipt-recovery",
                path,
                StringComparison.Ordinal));
        var terminal = Assert.IsType<
            AgentStateDb.PricingResultOutboxQuarantineEntry>(
            _db.GetPricingResultOutboxQuarantine(spec.JobId));
        Assert.Equal(
            "pricing_result_manual_reconciliation_required",
            terminal.ReasonCode);
        Assert.Empty(_db.GetAllPendingPricingResultPayloads(20));

        await uploader.FlushPendingAsync(
            CancellationToken.None,
            includeDeferred: true);
        Assert.Equal(4, signer.Paths.Count);
        Assert.Equal(3, CountAuthorityRecoveryAttempts(spec.JobId));
    }

    [Fact]
    public void SendAttemptMigration_RejectsTamperedGrantPair_AndIsImmutable()
    {
        var now = new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
        var uploader = CreateUploader(
            new RecordingPostSigner(),
            _db,
            new AuthorityTimeProvider(now.AddMinutes(1)));
        var (spec, _) = StageAuthorizedCompletedPayload(
            uploader,
            "attempt-ledger-pair-tamper",
            now,
            now.AddDays(7));
        var outbox = Assert.IsType<AgentStateDb.PricingResultOutboxEntry>(
            _db.GetPricingResultOutbox(spec.JobId));

        using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(_tempDir, "state.db")}");
        connection.Open();
        Assert.Throws<SqliteException>(() => InsertAuthorityAttempt(
            connection,
            spec.JobId,
            outbox.PayloadSha256,
            "99999999-9999-4999-8999-999999999999",
            new string('f', 64)));

        InsertAuthorityAttempt(
            connection,
            spec.JobId,
            outbox.PayloadSha256,
            spec.ApprovalId!,
            spec.GrantDigest!);
        using var mutation = connection.CreateCommand();
        mutation.CommandText = """
            UPDATE pricing_result_authority_send_attempts
               SET attempted_at_utc = @changed
             WHERE job_id = @job AND payload_sha256 = @payload
            """;
        mutation.Parameters.AddWithValue("@changed", now.AddDays(1).ToString("O"));
        mutation.Parameters.AddWithValue("@job", spec.JobId);
        mutation.Parameters.AddWithValue("@payload", outbox.PayloadSha256);
        Assert.Throws<SqliteException>(() => mutation.ExecuteNonQuery());
    }

    private async Task<string> WaitForAuthorityDenialAsync(
        PricingJobSpec spec,
        TimeProvider clock,
        TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (!_db.TryAdmitPricingJobAuthority(
                    spec.JobId,
                    spec.ApprovalId,
                    spec.GrantDigest,
                    clock.GetUtcNow(),
                    PricingTestAuthority.TrustedPublicKeys,
                    out var code))
                return code;
            await Task.Delay(1);
        }
        throw new TimeoutException("Pricing authority did not deny before timeout.");
    }

    private async Task WaitForRevocationCountAsync(
        string approvalId,
        int expected,
        TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (_db.GetPendingPricingApprovalRevocationCount(approvalId) == expected)
                return;
            await Task.Delay(1);
        }
        throw new TimeoutException("Pricing revocations did not enter before timeout.");
    }

    private int CountAuthorityRecoveryAttempts(string jobId)
    {
        using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(_tempDir, "state.db")}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT count(*)
              FROM pricing_result_authority_recovery_attempts
             WHERE job_id = @job
            """;
        command.Parameters.AddWithValue("@job", jobId);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static void InsertAuthorityAttempt(
        SqliteConnection connection,
        string jobId,
        string payloadSha256,
        string approvalId,
        string grantDigest)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO pricing_result_authority_send_attempts (
                job_id, payload_sha256, approval_id, grant_digest,
                attempted_at_utc)
            VALUES (@job, @payload, @approval, @grant, @attempted)
            """;
        command.Parameters.AddWithValue("@job", jobId);
        command.Parameters.AddWithValue("@payload", payloadSha256);
        command.Parameters.AddWithValue("@approval", approvalId);
        command.Parameters.AddWithValue("@grant", grantDigest);
        command.Parameters.AddWithValue(
            "@attempted", DateTimeOffset.UtcNow.ToString("O"));
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private static VerifiedCloudPostResponse BuildSuccessResponse(
        string path,
        JsonElement payload,
        bool idempotent)
    {
        var jobId = path.Split('/', StringSplitOptions.RemoveEmptyEntries)[3];
        var commandId = payload.GetProperty("commandId").GetString();
        var recorded = idempotent
            ? 1
            : payload.GetProperty("items").GetArrayLength();
        var body = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            kind = "pricing_result_receipt",
            accepted = true,
            commandId,
            agentInstanceId = AgentId,
            pharmacyId = PharmacyId,
            jobId,
            recorded,
            idempotent,
        });
        return VerifiedResponse(200, body);
    }

    private sealed class BlockingSuccessSigner : IPostSigner
    {
        private readonly TaskCompletionSource<bool> _entered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public string? BoundAgentInstanceId => AgentId;
        public string? BoundPharmacyId => PharmacyId;
        internal Task Entered => _entered.Task;
        internal int CallCount { get; private set; }

        internal void Release() => _release.TrySetResult(true);

        public Task<JsonElement?> PostSignedAsync(
            string path,
            object payload,
            CancellationToken ct) => Task.FromResult<JsonElement?>(null);

        public Task<JsonElement?> PostSignedVerifiedAsync(
            string path,
            object payload,
            string publicKeyDer,
            CancellationToken ct) => Task.FromResult<JsonElement?>(null);

        public async Task<VerifiedCloudPostResponse?>
            PostSignedResponseVerifiedAsync(
                string path,
                object payload,
                CancellationToken ct)
        {
            var json = JsonSerializer.SerializeToElement(payload);
            CallCount++;
            _entered.TrySetResult(true);
            await _release.Task.WaitAsync(ct);
            return BuildSuccessResponse(path, json, idempotent: false);
        }
    }

    private sealed class SequencedRecoverySigner(int unsignedResponses) : IPostSigner
    {
        private int _remainingUnsigned = unsignedResponses;

        public string? BoundAgentInstanceId => AgentId;
        public string? BoundPharmacyId => PharmacyId;
        internal List<string> Paths { get; } = [];
        internal List<JsonElement> Payloads { get; } = [];

        public Task<JsonElement?> PostSignedAsync(
            string path,
            object payload,
            CancellationToken ct) => Task.FromResult<JsonElement?>(null);

        public Task<JsonElement?> PostSignedVerifiedAsync(
            string path,
            object payload,
            string publicKeyDer,
            CancellationToken ct) => Task.FromResult<JsonElement?>(null);

        public Task<VerifiedCloudPostResponse?> PostSignedResponseVerifiedAsync(
            string path,
            object payload,
            CancellationToken ct)
        {
            var json = JsonSerializer.SerializeToElement(payload);
            Paths.Add(path);
            Payloads.Add(json);
            if (_remainingUnsigned > 0)
            {
                _remainingUnsigned--;
                return Task.FromResult<VerifiedCloudPostResponse?>(null);
            }
            return Task.FromResult<VerifiedCloudPostResponse?>(
                BuildSuccessResponse(
                    path,
                    json,
                    idempotent: path.EndsWith(
                        "/receipt-recovery",
                        StringComparison.Ordinal)));
        }
    }
}
