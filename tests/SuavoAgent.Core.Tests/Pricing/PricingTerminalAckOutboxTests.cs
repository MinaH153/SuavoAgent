using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Pricing;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Pricing;

public sealed class PricingTerminalAckOutboxTests
{
    private const string CommandId = "40000000-0000-4000-8000-000000000001";
    private const string AgentId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    private const string PharmacyId = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";

    [Fact]
    public async Task First_transport_loss_persists_exact_phi_free_failure()
    {
        using var db = new AgentStateDb(":memory:");
        var attempts = 0;
        var outbox = CreateOutbox(db, (_, _, _, _, _) =>
        {
            attempts++;
            return Task.FromResult(false);
        });

        await outbox.StageAndTryDeliverAsync(
            CommandId,
            PricingTerminalAck.Early("pricing_executor_unavailable"),
            CancellationToken.None);

        var persisted = Assert.IsType<AgentStateDb.PricingTerminalAckOutboxEntry>(
            db.GetPricingTerminalAck(CommandId));
        Assert.Equal(1, attempts);
        Assert.Equal("pending", persisted.State);
        Assert.Equal(1, persisted.AttemptCount);
        Assert.Equal("none", persisted.Ack.ResultKind);
        Assert.Equal("pricing_executor_unavailable", persisted.Ack.ErrorCode);
        Assert.Null(persisted.Ack.BuildResult());
        Assert.DoesNotContain("path", JsonSerializer.Serialize(persisted),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Retry_after_restart_delivers_without_an_execution_dependency()
    {
        var path = Path.Combine(
            Path.GetTempPath(), $"suavo_pricing_ack_{Guid.NewGuid():N}.db");
        try
        {
            using (var first = new AgentStateDb(path))
            {
                var initial = CreateOutbox(
                    first,
                    (_, _, _, _, _) => Task.FromResult(false));
                await initial.StageAndTryDeliverAsync(
                    CommandId,
                    PricingTerminalAck.Cancelled(),
                    CancellationToken.None);
            }

            var retryCalls = 0;
            using (var restarted = new AgentStateDb(path))
            {
                var recovery = CreateOutbox(restarted, (_, succeeded, result, error, _) =>
                {
                    retryCalls++;
                    Assert.False(succeeded);
                    Assert.Equal("pricing_cancelled", error);
                    Assert.Equal(
                        "cancelled",
                        JsonSerializer.SerializeToElement(result)
                            .GetProperty("status").GetString());
                    return Task.FromResult(true);
                });
                await recovery.RetryPendingAsync(
                    CancellationToken.None,
                    includeDeferred: true);

                var delivered = restarted.GetPricingTerminalAck(CommandId);
                Assert.NotNull(delivered);
                Assert.Equal("delivered", delivered!.State);
                Assert.NotNull(delivered.DeliveredAt);
            }
            Assert.Equal(1, retryCalls);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public async Task Crash_before_terminal_branch_recovers_failure_without_reexecution()
    {
        var path = Path.Combine(
            Path.GetTempPath(), $"suavo_pricing_intent_{Guid.NewGuid():N}.db");
        try
        {
            using (var first = new AgentStateDb(path))
            {
                var active = CreateOutbox(
                    first,
                    (_, _, _, _, _) => throw new Xunit.Sdk.XunitException(
                        "An active intent must not send before a terminal branch."));
                Assert.True(active.TryRegisterVerifiedCommand(
                    "crash-recovery-nonce",
                    CommandId,
                    "run_pricing_job"));
                // Simulate power loss: no executor result and no terminal ACK.
            }

            var sends = 0;
            using (var restarted = new AgentStateDb(path))
            {
                var recovery = CreateOutbox(
                    restarted,
                    (_, succeeded, result, error, _) =>
                    {
                        sends++;
                        Assert.False(succeeded);
                        Assert.Null(result);
                        Assert.Equal("pricing_execution_exception", error);
                        return Task.FromResult(true);
                    });
                await recovery.RetryPendingAsync(
                    CancellationToken.None,
                    includeDeferred: true);
                Assert.Equal("delivered",
                    restarted.GetPricingTerminalAck(CommandId)!.State);
            }
            Assert.Equal(1, sends);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public async Task Result_pending_recovery_waits_then_terminal_retry_stages_failure()
    {
        var path = Path.Combine(
            Path.GetTempPath(), $"suavo_pricing_result_recovery_{Guid.NewGuid():N}.db");
        PricingJobSpec spec;
        PricingJobExecutionResult execution;
        try
        {
            using (var first = new AgentStateDb(path))
            {
                var active = CreateOutbox(
                    first,
                    (_, _, _, _, _) => Task.FromResult(false));
                var pair = CompletedPricing(first);
                spec = pair.Spec;
                execution = pair.Execution;
                Assert.True(active.TryRegisterVerifiedCommand(
                    "result-recovery-nonce",
                    CommandId,
                    "run_pricing_job",
                    spec.ApprovalId,
                    spec.GrantDigest));
                var transientUploader = CreateUploader(
                    new ResultSigner(terminal: false), first);
                transientUploader.PrepareDelivery(
                    spec, CommandId, null, PricingExecutorMode.SqlFirst);
                var transient = await transientUploader.UploadAsync(
                    spec, execution, CommandId, CancellationToken.None);
                Assert.False(transient.VerifiedTerminal);
                active.MarkResultPending(CommandId);
            }

            using var restarted = new AgentStateDb(path);
            var sends = 0;
            object? sentResult = null;
            var recovery = CreateOutbox(
                restarted,
                (_, _, result, _, _) =>
                {
                    sends++;
                    sentResult = result;
                    return Task.FromResult(true);
                });

            await recovery.RetryPendingAsync(
                CancellationToken.None,
                includeDeferred: true);
            Assert.Equal(0, sends);
            Assert.Null(restarted.GetPricingTerminalAck(CommandId));

            var terminalUploader = CreateUploader(
                new ResultSigner(terminal: true), restarted);
            await terminalUploader.FlushPendingAsync(
                CancellationToken.None,
                includeDeferred: true);
            await recovery.RetryPendingAsync(
                CancellationToken.None,
                includeDeferred: true);

            Assert.Equal(1, sends);
            var result = JsonSerializer.SerializeToElement(sentResult);
            Assert.Equal("pricing_failed", result.GetProperty("status").GetString());
            Assert.Equal("pricing_job_failed", result.GetProperty("reason").GetString());
            Assert.Equal("delivered",
                restarted.GetPricingTerminalAck(CommandId)!.State);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public async Task Current_process_result_pending_converges_after_terminal_retry()
    {
        using var db = new AgentStateDb(":memory:");
        var sends = 0;
        object? sentResult = null;
        var outbox = CreateOutbox(
            db,
            (_, _, result, _, _) =>
            {
                sends++;
                sentResult = result;
                return Task.FromResult(true);
            });
        var (spec, execution) = CompletedPricing(db);
        Assert.True(outbox.TryRegisterVerifiedCommand(
            "current-process-result-nonce",
            CommandId,
            "run_pricing_job",
            spec.ApprovalId,
            spec.GrantDigest));
        var transientUploader = CreateUploader(
            new ResultSigner(terminal: false), db);
        transientUploader.PrepareDelivery(
            spec, CommandId, null, PricingExecutorMode.SqlFirst);
        var transient = await transientUploader.UploadAsync(
            spec, execution, CommandId, CancellationToken.None);
        Assert.False(transient.VerifiedTerminal);
        outbox.MarkResultPending(CommandId);

        await outbox.RetryPendingAsync(
            CancellationToken.None,
            includeDeferred: true);
        Assert.Equal(0, sends);

        var terminalUploader = CreateUploader(
            new ResultSigner(terminal: true), db);
        await terminalUploader.FlushPendingAsync(
            CancellationToken.None,
            includeDeferred: true);
        await outbox.RetryPendingAsync(
            CancellationToken.None,
            includeDeferred: true);

        Assert.Equal(1, sends);
        Assert.Equal(
            "pricing_failed",
            JsonSerializer.SerializeToElement(sentResult)
                .GetProperty("status").GetString());
        Assert.Equal("delivered", db.GetPricingTerminalAck(CommandId)!.State);
    }

    [Fact]
    public void Pricing_recovery_migrations_apply_to_database_recorded_at_26()
    {
        var path = Path.Combine(
            Path.GetTempPath(), $"suavo_pricing_migration_{Guid.NewGuid():N}.db");
        try
        {
            using (var initialized = new AgentStateDb(path)) { }
            using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                connection.Open();
                using var downgrade = connection.CreateCommand();
                downgrade.CommandText = """
                    DROP TRIGGER IF EXISTS
                        pricing_job_authority_identity_immutable;
                    DROP TRIGGER IF EXISTS
                        pricing_input_identity_authority_binding_coherent;
                    DROP TRIGGER IF EXISTS
                        pricing_result_delivery_intent_immutable;
                    DROP TRIGGER IF EXISTS
                        pricing_result_authority_recovery_evidence_required;
                    DROP TRIGGER IF EXISTS
                        pricing_result_authority_recovery_immutable;
                    DROP TRIGGER IF EXISTS
                        pricing_result_authority_recovery_no_delete;
                    DROP TABLE pricing_result_authority_recovery_attempts;
                    DROP TABLE pricing_result_authority_send_attempts;
                    ALTER TABLE pricing_jobs DROP COLUMN grant_digest;
                    ALTER TABLE pricing_jobs DROP COLUMN approval_id;
                    ALTER TABLE pricing_job_input_identity
                        DROP COLUMN authority_approval_id;
                    ALTER TABLE pricing_result_delivery_intents
                        DROP COLUMN grant_digest;
                    ALTER TABLE pricing_result_delivery_intents
                        DROP COLUMN approval_id;
                    DROP TABLE pricing_command_execution_intents;
                    DELETE FROM schema_migrations
                     WHERE version IN (27, 31, 32, 39, 40);
                    """;
                downgrade.ExecuteNonQuery();
            }

            using var upgraded = new AgentStateDb(path);
            var outbox = CreateOutbox(
                upgraded,
                (_, _, _, _, _) => Task.FromResult(true));
            Assert.True(outbox.TryRegisterVerifiedCommand(
                "migration-27-nonce",
                CommandId,
                "run_pricing_job"));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public async Task Transient_result_sync_stays_only_in_result_outbox()
    {
        using var db = new AgentStateDb(":memory:");
        var signer = new ResultSigner(terminal: false);
        var uploader = CreateUploader(signer, db);
        var (spec, execution) = CompletedPricing(db);
        RegisterPricingCommand(db, spec);
        uploader.PrepareDelivery(
            spec, CommandId, null, PricingExecutorMode.SqlFirst);

        var receipt = await uploader.UploadAsync(
            spec, execution, CommandId, CancellationToken.None);

        Assert.False(receipt.Accepted);
        Assert.False(receipt.VerifiedTerminal);
        Assert.Equal("pending", db.GetPricingResultOutbox(spec.JobId)!.State);
        Assert.Null(PricingTerminalAckPolicy.FromResultSync(
            receipt, spec.JobId, execution));
        Assert.Empty(db.GetPendingPricingTerminalAcks(20, includeDeferred: true));
    }

    [Fact]
    public async Task Verified_terminal_result_sync_stages_finite_failure()
    {
        using var db = new AgentStateDb(":memory:");
        var signer = new ResultSigner(terminal: true);
        var uploader = CreateUploader(signer, db);
        var (spec, execution) = CompletedPricing(db);
        RegisterPricingCommand(db, spec);
        uploader.PrepareDelivery(
            spec, CommandId, null, PricingExecutorMode.SqlFirst);
        var receipt = await uploader.UploadAsync(
            spec, execution, CommandId, CancellationToken.None);

        Assert.True(receipt.VerifiedTerminal);
        var terminalAck = Assert.IsType<PricingTerminalAck>(
            PricingTerminalAckPolicy.FromResultSync(
                receipt, spec.JobId, execution));
        var outbox = CreateOutbox(
            db,
            (_, _, _, _, _) => Task.FromResult(false));
        await outbox.StageAndTryDeliverAsync(
            CommandId, terminalAck, CancellationToken.None);

        var persisted = db.GetPricingTerminalAck(CommandId);
        Assert.NotNull(persisted);
        Assert.Equal("pricing_failed", persisted!.Ack.ResultKind);
        Assert.Equal("pricing_job_failed", persisted.Ack.ErrorCode);
        Assert.Equal("sql", persisted.Ack.Mode);
        Assert.Equal(spec.JobId, persisted.Ack.JobId);
    }

    [Theory]
    [InlineData("revoked", "pricing_cost_basis_approval_revoked")]
    [InlineData("expired", "pricing_cost_basis_approval_expired")]
    [InlineData("binding_missing", "pricing_job_authority_binding_missing")]
    public async Task AuthorityTerminalReceipt_AfterCrashRecoversExactEarlyAck(
        string scenario,
        string expectedCode)
    {
        var path = Path.Combine(
            Path.GetTempPath(), $"suavo_pricing_authority_crash_{Guid.NewGuid():N}.db");
        var now = new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
        var resultSigner = new ResultSigner(terminal: false);
        try
        {
            using (var first = new AgentStateDb(path))
            {
                var initialOutbox = CreateOutbox(
                    first,
                    (_, _, _, _, _) => Task.FromResult(false));
                var uploader = CreateUploader(
                    resultSigner,
                    first,
                    new FixedTimeProvider(now.AddMinutes(2)));
                var spec = new PricingJobSpec(
                    Guid.NewGuid().ToString("N"),
                    @"C:\Pricing.xlsx",
                    "NDC",
                    "Supplier",
                    "Cost");
                PricingApprovalGrant? grant = null;
                PricingCostBasisAuthority? authority = null;
                PricingObservationContract? contract = null;
                if (scenario == "binding_missing")
                {
                    Assert.True(first.RecordPricingCloudAuthorityHeartbeat(
                        now,
                        now,
                        out var leaseCode), leaseCode);
                    spec = spec with
                    {
                        ApprovalId = "48888888-8888-4888-8888-888888888888",
                        GrantDigest = new string('e', 64),
                    };
                }
                else
                {
                    contract = PricingTestAuthority.Contract();
                    grant = PricingTestAuthority.InstallApproval(
                        first,
                        contract,
                        now,
                        scenario == "expired"
                            ? now.AddMinutes(1)
                            : now.AddDays(7));
                    authority = PricingObservationPolicy.TryAdmitAuthority(
                        grant,
                        PricingTestAuthority.PharmacyId,
                        PricingTestAuthority.AgentId,
                        PricingTestAuthority.MachineFingerprint,
                        contract,
                        now,
                        PricingTestAuthority.TrustedPublicKeys,
                        out var authorityCode);
                    Assert.NotNull(authority);
                    Assert.Equal(
                        "pricing_cost_basis_approval_admitted",
                        authorityCode);
                    spec = spec with
                    {
                        ApprovalId = authority!.ApprovalId,
                        GrantDigest = authority.ApprovalDigest,
                    };
                }
                Assert.True(initialOutbox.TryRegisterVerifiedCommand(
                    $"authority-crash-{scenario}",
                    CommandId,
                    "run_pricing_job",
                    spec.ApprovalId,
                    spec.GrantDigest));
                uploader.PrepareDelivery(
                    spec,
                    CommandId,
                    null,
                    PricingExecutorMode.SqlFirst);
                if (scenario != "binding_missing")
                {
                    Assert.True(first.TryBindPricingInputIdentity(
                        spec.JobId,
                        new string('a', 64),
                        new string('b', 64),
                        contract!,
                        authority!,
                        now,
                        out var bindCode), bindCode);
                }
                first.SavePricingResult(new SupplierPriceResult(
                    spec.JobId,
                    2,
                    "55111064501",
                    true,
                    "McKesson",
                    1.25m,
                    null));
                first.UpsertPricingJob(
                    spec,
                    PricingJobStatus.Completed,
                    1,
                    1,
                    0);
                if (scenario == "binding_missing")
                {
                    var payload = PricingJobCloudUploader
                        .BuildPersistedPayloadEnvelope(
                            spec.JobId,
                            CommandId,
                            PricingJobStatus.Completed,
                            "sql",
                            1,
                            1,
                            0,
                            first.GetPricingResults(spec.JobId),
                            spec.ApprovalId,
                            spec.GrantDigest);
                    first.StagePricingResultPayload(
                        spec.JobId,
                        CommandId,
                        null,
                        payload.Json,
                        payload.ItemCount,
                        executionOk: true);
                }
                if (scenario == "revoked")
                {
                    var revoked = PricingTestAuthority.InstallRevocation(
                        first,
                        PricingTestAuthority.Revocation(
                            grant!,
                            now.AddMinutes(1)),
                        now.AddMinutes(1));
                    Assert.True(revoked.Succeeded, revoked.Code);
                }

                await uploader.FlushPendingAsync(
                    CancellationToken.None,
                    includeDeferred: true);
                Assert.Equal(
                    expectedCode,
                    first.GetPricingResultOutboxQuarantine(spec.JobId)?.ReasonCode);
                var evidence = first.GetPricingCommandRecoveryEvidence(CommandId);
                Assert.Equal(
                    AgentStateDb.PricingCommandRecoveryKind.ResultTerminal,
                    evidence.Kind);
                Assert.Equal(expectedCode, evidence.TerminalAck?.ErrorCode);
                // Simulated power loss here: terminal result receipt is durable,
                // but no pricing command ACK has been staged yet.
            }

            using var restarted = new AgentStateDb(path);
            var ackSends = 0;
            string? deliveredError = null;
            object? deliveredResult = new object();
            var recovery = CreateOutbox(
                restarted,
                (_, succeeded, result, error, _) =>
                {
                    ackSends++;
                    Assert.False(succeeded);
                    deliveredResult = result;
                    deliveredError = error;
                    return Task.FromResult(true);
                });

            await recovery.RetryPendingAsync(
                CancellationToken.None,
                includeDeferred: true);
            await recovery.RetryPendingAsync(
                CancellationToken.None,
                includeDeferred: true);

            Assert.Equal(1, ackSends);
            Assert.Null(deliveredResult);
            Assert.Equal(expectedCode, deliveredError);
            Assert.Equal(
                expectedCode,
                restarted.GetPricingTerminalAck(CommandId)!.Ack.ErrorCode);
            Assert.Equal(0, resultSigner.CallCount);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void Schema_rejects_free_form_or_path_bearing_failures()
    {
        Assert.Throws<ArgumentException>(() =>
            PricingTerminalAck.Early(@"C:\Patients\pricing.xlsx"));
        Assert.Throws<ArgumentException>(() =>
            PricingTerminalAck.DiscoveryFailed("jane_doe_hiv", false));
        Assert.Throws<InvalidOperationException>(() =>
        {
            using var db = new AgentStateDb(":memory:");
            db.StagePricingTerminalAck(
                CommandId,
                PricingTerminalAck.Cancelled());
            db.StagePricingTerminalAck(
                CommandId,
                PricingTerminalAck.Early("pricing_executor_unavailable"));
        });
    }

    [Fact]
    public void Pricing_failure_ack_persists_explicit_package_cost_basis()
    {
        using var db = new AgentStateDb(":memory:");
        var ack = PricingTerminalAck.PricingFailed(
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "uia",
            10,
            8,
            2,
            "pricing_job_failed",
            PricingApprovalContract.PackageCostBasis);

        var persisted = db.StagePricingTerminalAck(CommandId, ack);

        Assert.Equal(
            PricingApprovalContract.PackageCostBasis,
            persisted.Ack.CostBasis);
        var json = JsonSerializer.Serialize(persisted.Ack.BuildResult());
        Assert.Contains("\"costBasis\":\"package_cost\"", json);
    }

    [Theory]
    [InlineData("pricing_worklist_source_unavailable")]
    [InlineData("pricing_worklist_generation_failed")]
    [InlineData("pricing_worklist_validation_failed")]
    [InlineData("pricing_worklist_empty")]
    [InlineData("pricing_report_permission_blocked")]
    [InlineData("pricing_pioneerrx_not_open")]
    [InlineData("pricing_report_open_failed")]
    [InlineData("pricing_report_filters_failed")]
    [InlineData("pricing_report_generation_failed")]
    [InlineData("pricing_report_export_failed")]
    [InlineData("pricing_report_save_dialog_blocked")]
    [InlineData("pricing_report_storage_unavailable")]
    [InlineData("pricing_report_validation_failed")]
    [InlineData("pricing_report_cancelled")]
    [InlineData("pricing_output_publication_failed")]
    public void Generated_worklist_failures_remain_finite_early_ack_codes(string code)
    {
        using var db = new AgentStateDb(":memory:");
        var persisted = db.StagePricingTerminalAck(
            CommandId,
            PricingTerminalAck.Early(code));

        Assert.Equal(code, persisted.Ack.ErrorCode);
    }

    [Fact]
    public void Package_surface_failure_remains_a_finite_terminal_reason()
    {
        var ack = PricingTerminalAck.PricingFailed(
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "uia",
            10,
            3,
            0,
            "pricing_package_cost_surface_unavailable",
            PricingApprovalContract.PackageCostBasis);

        Assert.Equal("pricing_package_cost_surface_unavailable", ack.ReasonCode);
        Assert.Equal(PricingApprovalContract.PackageCostBasis, ack.CostBasis);
    }

    private static PricingTerminalAckOutbox CreateOutbox(
        AgentStateDb db,
        Func<string, bool, object?, string?, CancellationToken, Task<bool>> send) =>
        new(db, send, NullLogger<PricingTerminalAckOutbox>.Instance);

    private static void RegisterPricingCommand(
        AgentStateDb db,
        PricingJobSpec spec)
    {
        Assert.True(db.TryRecordNonceAndRegisterPricingIntent(
            Guid.NewGuid().ToString("N"),
            CommandId,
            "run_pricing_job",
            Guid.NewGuid().ToString("N"),
            verifiedCommand: null,
            spec.ApprovalId,
            spec.GrantDigest));
    }

    private static PricingJobCloudUploader CreateUploader(
        IPostSigner signer,
        AgentStateDb db,
        TimeProvider? clock = null) => new(
            signer,
            db,
            NullLogger<PricingJobCloudUploader>.Instance,
            PricingTestAuthority.TrustedPublicKeys,
            clock);

    private static (PricingJobSpec Spec, PricingJobExecutionResult Execution)
        CompletedPricing(AgentStateDb db)
    {
        var jobId = Guid.NewGuid().ToString("N");
        var evaluatedAt = DateTimeOffset.UtcNow;
        var contract = PricingTestAuthority.Contract();
        var authority = PricingTestAuthority.InstallAuthority(
            db, contract, evaluatedAt);
        var spec = new PricingJobSpec(
            jobId, @"C:\Pricing.xlsx", "NDC", "Supplier", "Cost",
            authority.ApprovalId, authority.ApprovalDigest);
        db.UpsertPricingJob(spec, PricingJobStatus.Running, 1, 0, 0);
        Assert.True(db.TryBindPricingInputIdentity(
            jobId,
            new string('a', 64),
            new string('b', 64),
            contract,
            authority,
            evaluatedAt,
            out var code), code);
        db.SavePricingResult(new SupplierPriceResult(
            jobId, 2, "55111064501", true, "supplier", 1.25m, null));
        db.UpsertPricingJob(spec, PricingJobStatus.Completed, 1, 1, 0);
        return (
            spec,
            new PricingJobExecutionResult(
                new PricingJobProgress(
                    jobId, 1, 1, 0, PricingJobStatus.Completed),
                "sql",
                true,
                null));
    }

    private sealed class ResultSigner(bool terminal) : IPostSigner
    {
        public string? BoundAgentInstanceId => AgentId;
        public string? BoundPharmacyId => PharmacyId;
        internal int CallCount { get; private set; }

        public Task<JsonElement?> PostSignedAsync(
            string path, object payload, CancellationToken ct) =>
            Task.FromResult<JsonElement?>(null);

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
            CallCount++;
            if (!terminal)
                return Task.FromResult<VerifiedCloudPostResponse?>(null);
            const string body =
                "{\"accepted\":false,\"terminal\":true," +
                "\"code\":\"pricing_result_payload_invalid\"," +
                "\"error\":\"Pricing result payload is invalid\"}";
            var digest = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
            return Task.FromResult<VerifiedCloudPostResponse?>(new(
                422,
                body,
                digest,
                RemoteCommandTrust.CommandV1KeyId,
                Convert.ToBase64String(new byte[64])));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
