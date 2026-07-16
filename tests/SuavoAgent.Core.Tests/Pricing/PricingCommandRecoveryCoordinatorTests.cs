using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Pricing;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Pricing;

public sealed class PricingCommandRecoveryCoordinatorTests
{
    private const string CommandId = "41111111-1111-4111-8111-111111111111";

    [Fact]
    public async Task Restart_resumes_exact_signed_admitted_checkpoint_once_without_nonce_replay()
    {
        var path = Path.Combine(
            Path.GetTempPath(), $"suavo_pricing_resume_{Guid.NewGuid():N}.db");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        const string keyId = "pricing-recovery-test";
        var trustedKeys = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [keyId] = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
            [PricingTestAuthority.KeyId] =
                PricingTestAuthority.TrustedPublicKeys[PricingTestAuthority.KeyId],
        };
        var scopeDigest = new string('7', 64);
        var spec = new PricingJobSpec(
            Guid.NewGuid().ToString("N"),
            @"C:\Pricing.xlsx",
            "NDC",
            "Supplier",
            "Cost Per Unit");
        var command = SignedPricingCommand(key, keyId);
        try
        {
            using (var first = new AgentStateDb(path))
            {
                var firstOutbox = new PricingTerminalAckOutbox(
                    first,
                    (_, _, _, _, _) => Task.FromResult(false),
                    NullLogger<PricingTerminalAckOutbox>.Instance,
                    trustedKeys);
                var contract = PricingTestAuthority.Contract(modality: "sql");
                var authority = PricingTestAuthority.InstallAuthority(
                    first,
                    contract);
                spec = spec with
                {
                    ApprovalId = authority.ApprovalId,
                    GrantDigest = authority.ApprovalDigest,
                };
                Assert.True(firstOutbox.TryRegisterVerifiedCommand(
                    command,
                    CommandId,
                    "run_pricing_job",
                    spec.ApprovalId,
                    spec.GrantDigest));
                first.PreparePricingResultDelivery(
                    spec,
                    CommandId,
                    sourceUploadId: null,
                    sourceMode: "sql");
                Assert.True(first.TryBindPricingInputIdentity(
                    spec.JobId,
                    new string('a', 64),
                    new string('b', 64),
                    contract,
                    authority,
                    DateTimeOffset.UtcNow,
                    out _));
                first.UpsertPricingJob(
                    spec,
                    PricingJobStatus.Running,
                    1,
                    0,
                    0);
                Assert.True(first.MarkPricingCommandIntentAdmitted(
                    CommandId,
                    "sql",
                    "supervised",
                    scopeDigest,
                    trustedIdentity: true));
            }

            using var restarted = new AgentStateDb(path);
            var sends = 0;
            var outbox = new PricingTerminalAckOutbox(
                restarted,
                (_, _, _, _, _) =>
                {
                    sends++;
                    return Task.FromResult(true);
                },
                NullLogger<PricingTerminalAckOutbox>.Instance,
                trustedKeys);

            // The retry-only worker must retain a valid resume checkpoint for
            // Heartbeat instead of racing it with a synthetic failure ACK.
            await outbox.RetryPendingAsync(
                CancellationToken.None,
                includeDeferred: true);
            Assert.Equal(0, sends);
            Assert.Null(restarted.GetPricingTerminalAck(CommandId));

            var executor = new RecoverableFailureExecutor(spec);
            var coordinator = new PricingCommandRecoveryCoordinator(
                restarted,
                executor,
                uploader: null,
                outbox,
                () => scopeDigest,
                NullLogger<PricingCommandRecoveryCoordinator>.Instance,
                trustedKeys);

            await coordinator.RecoverAsync(CancellationToken.None);
            await coordinator.RecoverAsync(CancellationToken.None);

            Assert.Equal(1, executor.RunCalls);
            Assert.Equal(1, sends);
            Assert.Equal(
                "delivered",
                restarted.GetPricingTerminalAck(CommandId)!.State);
            Assert.Equal(1, CountNonce(path, command.Nonce));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public async Task Restart_terminalizes_expired_signed_command_without_execution()
    {
        var path = Path.Combine(
            Path.GetTempPath(), $"suavo_pricing_command_expiry_{Guid.NewGuid():N}.db");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        const string keyId = "pricing-command-expiry-test";
        var trustedKeys = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [keyId] = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
            [PricingTestAuthority.KeyId] =
                PricingTestAuthority.TrustedPublicKeys[PricingTestAuthority.KeyId],
        };
        var scopeDigest = new string('7', 64);
        var spec = NewSpec();
        var command = SignedPricingCommand(key, keyId);
        try
        {
            var seeded = SeedAbandonedIntent(
                path,
                trustedKeys,
                command,
                spec,
                scopeDigest,
                DateTimeOffset.Parse(command.Timestamp).AddHours(1));
            spec = seeded.Spec;

            using var restarted = new AgentStateDb(path);
            string? deliveredError = null;
            var outbox = new PricingTerminalAckOutbox(
                restarted,
                (_, succeeded, _, error, _) =>
                {
                    Assert.False(succeeded);
                    deliveredError = error;
                    return Task.FromResult(true);
                },
                NullLogger<PricingTerminalAckOutbox>.Instance,
                trustedKeys);
            var executor = new RecoverableFailureExecutor(spec);
            var coordinator = new PricingCommandRecoveryCoordinator(
                restarted,
                executor,
                uploader: null,
                outbox,
                () => scopeDigest,
                NullLogger<PricingCommandRecoveryCoordinator>.Instance,
                trustedKeys,
                new FixedTimeProvider(
                    DateTimeOffset.Parse(command.ExpiresAt!).AddSeconds(1)));

            await coordinator.RecoverAsync(CancellationToken.None);

            Assert.Equal(0, executor.RunCalls);
            Assert.Equal("pricing_command_authority_expired", deliveredError);
            Assert.Equal(
                "pricing_command_authority_expired",
                restarted.GetPricingTerminalAck(CommandId)!.Ack.ErrorCode);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public async Task Restart_terminalizes_expired_pic_grant_without_execution()
    {
        var path = Path.Combine(
            Path.GetTempPath(), $"suavo_pricing_grant_expiry_{Guid.NewGuid():N}.db");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        const string keyId = "pricing-grant-expiry-test";
        var trustedKeys = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [keyId] = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
            [PricingTestAuthority.KeyId] =
                PricingTestAuthority.TrustedPublicKeys[PricingTestAuthority.KeyId],
        };
        var scopeDigest = new string('7', 64);
        var spec = NewSpec();
        var command = SignedPricingCommand(key, keyId);
        var issuedAt = DateTimeOffset.Parse(command.Timestamp);
        try
        {
            var seeded = SeedAbandonedIntent(
                path,
                trustedKeys,
                command,
                spec,
                scopeDigest,
                issuedAt.AddMinutes(1));
            spec = seeded.Spec;

            using var restarted = new AgentStateDb(path);
            string? deliveredError = null;
            var outbox = new PricingTerminalAckOutbox(
                restarted,
                (_, _, _, error, _) =>
                {
                    deliveredError = error;
                    return Task.FromResult(true);
                },
                NullLogger<PricingTerminalAckOutbox>.Instance,
                trustedKeys);
            var executor = new RecoverableFailureExecutor(spec);
            var coordinator = new PricingCommandRecoveryCoordinator(
                restarted,
                executor,
                uploader: null,
                outbox,
                () => scopeDigest,
                NullLogger<PricingCommandRecoveryCoordinator>.Instance,
                trustedKeys,
                new FixedTimeProvider(issuedAt.AddMinutes(2)));

            await coordinator.RecoverAsync(CancellationToken.None);

            Assert.Equal(0, executor.RunCalls);
            Assert.Equal("pricing_cost_basis_approval_expired", deliveredError);
            Assert.Equal(
                "pricing_cost_basis_approval_expired",
                restarted.GetPricingTerminalAck(CommandId)!.Ack.ErrorCode);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public async Task Restart_terminalizes_revoked_exact_pic_grant_without_execution()
    {
        var path = Path.Combine(
            Path.GetTempPath(), $"suavo_pricing_grant_revoke_{Guid.NewGuid():N}.db");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        const string keyId = "pricing-grant-revoke-test";
        var trustedKeys = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [keyId] = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
            [PricingTestAuthority.KeyId] =
                PricingTestAuthority.TrustedPublicKeys[PricingTestAuthority.KeyId],
        };
        var scopeDigest = new string('7', 64);
        var spec = NewSpec();
        var command = SignedPricingCommand(key, keyId);
        var issuedAt = DateTimeOffset.Parse(command.Timestamp);
        try
        {
            var seeded = SeedAbandonedIntent(
                path,
                trustedKeys,
                command,
                spec,
                scopeDigest,
                issuedAt.AddDays(7));
            spec = seeded.Spec;
            using (var revocationDb = new AgentStateDb(path))
            {
                var revokedAt = issuedAt.AddMinutes(1);
                var revoked = PricingTestAuthority.InstallRevocation(
                    revocationDb,
                    PricingTestAuthority.Revocation(
                        Assert.IsType<PricingApprovalGrant>(seeded.Grant),
                        revokedAt),
                    revokedAt);
                Assert.True(revoked.Succeeded, revoked.Code);
            }

            using var restarted = new AgentStateDb(path);
            string? deliveredError = null;
            var outbox = new PricingTerminalAckOutbox(
                restarted,
                (_, _, _, error, _) =>
                {
                    deliveredError = error;
                    return Task.FromResult(true);
                },
                NullLogger<PricingTerminalAckOutbox>.Instance,
                trustedKeys);
            var executor = new RecoverableFailureExecutor(spec);
            var coordinator = new PricingCommandRecoveryCoordinator(
                restarted,
                executor,
                uploader: null,
                outbox,
                () => scopeDigest,
                NullLogger<PricingCommandRecoveryCoordinator>.Instance,
                trustedKeys,
                new FixedTimeProvider(issuedAt.AddMinutes(2)));

            await coordinator.RecoverAsync(CancellationToken.None);

            Assert.Equal(0, executor.RunCalls);
            Assert.Equal("pricing_cost_basis_approval_revoked", deliveredError);
            Assert.Equal(
                "pricing_cost_basis_approval_revoked",
                restarted.GetPricingTerminalAck(CommandId)!.Ack.ErrorCode);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public async Task Restart_terminalizes_missing_job_authority_binding_without_execution()
    {
        var path = Path.Combine(
            Path.GetTempPath(), $"suavo_pricing_binding_missing_{Guid.NewGuid():N}.db");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        const string keyId = "pricing-binding-missing-test";
        var trustedKeys = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [keyId] = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
            [PricingTestAuthority.KeyId] =
                PricingTestAuthority.TrustedPublicKeys[PricingTestAuthority.KeyId],
        };
        var scopeDigest = new string('7', 64);
        var spec = NewSpec();
        var command = SignedPricingCommand(key, keyId);
        var issuedAt = DateTimeOffset.Parse(command.Timestamp);
        try
        {
            var seeded = SeedAbandonedIntent(
                path,
                trustedKeys,
                command,
                spec,
                scopeDigest,
                issuedAt.AddDays(7),
                bindAuthority: false);
            spec = seeded.Spec;

            using var restarted = new AgentStateDb(path);
            string? deliveredError = null;
            var outbox = new PricingTerminalAckOutbox(
                restarted,
                (_, _, _, error, _) =>
                {
                    deliveredError = error;
                    return Task.FromResult(true);
                },
                NullLogger<PricingTerminalAckOutbox>.Instance,
                trustedKeys);
            var executor = new RecoverableFailureExecutor(spec);
            var coordinator = new PricingCommandRecoveryCoordinator(
                restarted,
                executor,
                uploader: null,
                outbox,
                () => scopeDigest,
                NullLogger<PricingCommandRecoveryCoordinator>.Instance,
                trustedKeys,
                new FixedTimeProvider(issuedAt.AddMinutes(2)));

            await coordinator.RecoverAsync(CancellationToken.None);

            Assert.Equal(0, executor.RunCalls);
            Assert.Equal("pricing_job_authority_binding_missing", deliveredError);
            Assert.Equal(
                "pricing_job_authority_binding_missing",
                restarted.GetPricingTerminalAck(CommandId)!.Ack.ErrorCode);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public async Task Restart_package_cost_refuses_completion_when_publication_fails()
    {
        var path = Path.Combine(
            Path.GetTempPath(), $"suavo_package_publish_recovery_{Guid.NewGuid():N}.db");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        const string keyId = "pricing-package-publication-test";
        var trustedKeys = TrustedKeys(key, keyId);
        var scopeDigest = new string('7', 64);
        var command = SignedPricingCommand(key, keyId);
        var spec = NewPackageSpec();
        try
        {
            spec = SeedAbandonedIntent(
                path,
                trustedKeys,
                command,
                spec,
                scopeDigest,
                DateTimeOffset.Parse(command.Timestamp).AddDays(1)).Spec;
            using var restarted = new AgentStateDb(path);
            string? deliveredError = null;
            var outbox = new PricingTerminalAckOutbox(
                restarted,
                (_, _, _, error, _) =>
                {
                    deliveredError = error;
                    return Task.FromResult(true);
                },
                NullLogger<PricingTerminalAckOutbox>.Instance,
                trustedKeys);
            var executor = new RecoverableSuccessExecutor(spec, @"C:\\Priced.xlsx");
            var publisher = new RecordingPricedWorkbookPublisher(
                published: false,
                throwAfterFirstPublication: false);
            var coordinator = new PricingCommandRecoveryCoordinator(
                restarted,
                executor,
                uploader: null,
                outbox,
                () => scopeDigest,
                NullLogger<PricingCommandRecoveryCoordinator>.Instance,
                trustedKeys,
                pricedWorkbookPublisher: publisher);

            await coordinator.RecoverAsync(CancellationToken.None);

            Assert.Equal(1, executor.RunCalls);
            Assert.Equal(1, publisher.Calls);
            Assert.Equal("pricing_output_publication_failed", deliveredError);
            Assert.Equal(
                "pricing_output_publication_failed",
                restarted.GetPricingTerminalAck(CommandId)!.Ack.ErrorCode);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public async Task Restart_package_cost_retries_idempotent_publication_after_crash()
    {
        var path = Path.Combine(
            Path.GetTempPath(), $"suavo_package_publish_retry_{Guid.NewGuid():N}.db");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        const string keyId = "pricing-package-publication-retry-test";
        var trustedKeys = TrustedKeys(key, keyId);
        var scopeDigest = new string('7', 64);
        var command = SignedPricingCommand(key, keyId);
        var spec = NewPackageSpec();
        try
        {
            spec = SeedAbandonedIntent(
                path,
                trustedKeys,
                command,
                spec,
                scopeDigest,
                DateTimeOffset.Parse(command.Timestamp).AddDays(1)).Spec;
            using var restarted = new AgentStateDb(path);
            string? deliveredError = null;
            var outbox = new PricingTerminalAckOutbox(
                restarted,
                (_, _, _, error, _) =>
                {
                    deliveredError = error;
                    return Task.FromResult(true);
                },
                NullLogger<PricingTerminalAckOutbox>.Instance,
                trustedKeys);
            var executor = new RecoverableSuccessExecutor(spec, @"C:\\Priced.xlsx");
            var publisher = new RecordingPricedWorkbookPublisher(
                published: true,
                throwAfterFirstPublication: true);
            var coordinator = new PricingCommandRecoveryCoordinator(
                restarted,
                executor,
                uploader: null,
                outbox,
                () => scopeDigest,
                NullLogger<PricingCommandRecoveryCoordinator>.Instance,
                trustedKeys,
                pricedWorkbookPublisher: publisher);

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => coordinator.RecoverAsync(CancellationToken.None));
            Assert.Null(restarted.GetPricingTerminalAck(CommandId));

            await coordinator.RecoverAsync(CancellationToken.None);

            Assert.Equal(2, executor.RunCalls);
            Assert.Equal(2, publisher.Calls);
            Assert.All(publisher.CommandIds, value => Assert.Equal(CommandId, value));
            Assert.Equal("pricing_execution_exception", deliveredError);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    private static PricingJobSpec NewSpec() => new(
        Guid.NewGuid().ToString("N"),
        @"C:\Pricing.xlsx",
        "NDC",
        "Supplier",
        "Cost Per Unit");

    private static PricingJobSpec NewPackageSpec() => new(
        Guid.NewGuid().ToString("N"),
        @"C:\Pricing.xlsx",
        PricingJobDefaults.NdcColumn,
        PricingJobDefaults.PackageSupplierColumn,
        PricingJobDefaults.PackageCostColumn,
        CostBasis: PricingApprovalContract.PackageCostBasis);

    private static IReadOnlyDictionary<string, string> TrustedKeys(
        ECDsa key,
        string keyId) => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [keyId] = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
        [PricingTestAuthority.KeyId] =
            PricingTestAuthority.TrustedPublicKeys[PricingTestAuthority.KeyId],
    };

    private static (PricingJobSpec Spec, PricingApprovalGrant? Grant)
        SeedAbandonedIntent(
        string path,
        IReadOnlyDictionary<string, string> trustedKeys,
        SignedCommand command,
        PricingJobSpec spec,
        string scopeDigest,
        DateTimeOffset authorityExpiresAt,
        bool bindAuthority = true)
    {
        using var first = new AgentStateDb(path);
        var outbox = new PricingTerminalAckOutbox(
            first,
            (_, _, _, _, _) => Task.FromResult(false),
            NullLogger<PricingTerminalAckOutbox>.Instance,
            trustedKeys);
        var packageCost = string.Equals(
            spec.CostBasis,
            PricingApprovalContract.PackageCostBasis,
            StringComparison.Ordinal);
        var executionMode = packageCost ? "uia" : "sql";
        var contract = PricingTestAuthority.Contract(
            modality: executionMode,
            costBasis: spec.CostBasis);
        var observedAt = DateTimeOffset.Parse(command.Timestamp);
        PricingApprovalGrant? grant = null;
        PricingCostBasisAuthority? authority = null;
        if (bindAuthority)
        {
            grant = PricingTestAuthority.InstallApproval(
                first,
                contract,
                observedAt,
                authorityExpiresAt);
            authority = PricingObservationPolicy.TryAdmitAuthority(
                grant,
                PricingTestAuthority.PharmacyId,
                PricingTestAuthority.AgentId,
                PricingTestAuthority.MachineFingerprint,
                contract,
                observedAt,
                PricingTestAuthority.TrustedPublicKeys,
                out var authorityCode);
            Assert.NotNull(authority);
            Assert.Equal("pricing_cost_basis_approval_admitted", authorityCode);
            spec = spec with
            {
                ApprovalId = authority!.ApprovalId,
                GrantDigest = authority.ApprovalDigest,
            };
        }
        else
        {
            Assert.True(first.RecordPricingCloudAuthorityHeartbeat(
                observedAt,
                observedAt,
                out var leaseCode), leaseCode);
            spec = spec with
            {
                ApprovalId = "49999999-9999-4999-8999-999999999999",
                GrantDigest = new string('f', 64),
            };
        }
        Assert.True(outbox.TryRegisterVerifiedCommand(
            command,
            CommandId,
            "run_pricing_job",
            spec.ApprovalId,
            spec.GrantDigest));
        first.PreparePricingResultDelivery(
            spec,
            CommandId,
            sourceUploadId: null,
            sourceMode: executionMode);
        if (bindAuthority)
        {
            Assert.True(first.TryBindPricingInputIdentity(
                spec.JobId,
                new string('a', 64),
                new string('b', 64),
                contract,
                authority!,
                observedAt,
                out _));
        }
        first.UpsertPricingJob(spec, PricingJobStatus.Running, 1, 0, 0);
        Assert.True(first.MarkPricingCommandIntentAdmitted(
            CommandId,
            executionMode,
            "supervised",
            scopeDigest,
            trustedIdentity: true));
        return (spec, grant);
    }

    private static SignedCommand SignedPricingCommand(ECDsa key, string keyId)
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("O");
        var nonce = $"pricing-recovery-{Guid.NewGuid():N}";
        var dataHash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes("exact-pricing-command-data")))
            .ToLowerInvariant();
        var canonical = RemoteCommandTrust.BuildCommandCanonical(
            "run_pricing_job",
            "agent-test",
            "machine-test",
            timestamp,
            nonce,
            dataHash);
        var signature = Convert.ToBase64String(key.SignData(
            Encoding.UTF8.GetBytes(canonical),
            HashAlgorithmName.SHA256));
        return new SignedCommand(
            "run_pricing_job",
            "agent-test",
            "machine-test",
            timestamp,
            nonce,
            keyId,
            signature,
            dataHash,
            DateTimeOffset.Parse(timestamp).AddMinutes(4).ToString("O"));
    }

    private static int CountNonce(string path, string nonce)
    {
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT count(*) FROM command_nonces WHERE nonce = @nonce";
        command.Parameters.AddWithValue("@nonce", nonce);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private sealed class RecoverableFailureExecutor(PricingJobSpec spec) :
        IPricingJobExecutor,
        IRecoverablePricingJobExecutor
    {
        internal int RunCalls { get; private set; }

        public Task<PricingJobExecutionResult> RunAsync(
            PricingJobSpec requested,
            CancellationToken ct)
        {
            RunCalls++;
            Assert.Equal(spec, requested);
            return Task.FromResult(new PricingJobExecutionResult(
                new PricingJobProgress(
                    spec.JobId,
                    1,
                    0,
                    1,
                    PricingJobStatus.Failed,
                    "pricing_job_failed"),
                "sql",
                false,
                "pricing job failed"));
        }

        public PricingJobSpec? GetRecoverableSpec(
            PricingJobSpec proposed,
            string? commandId) => spec;

        public PricingJobSpec? GetRecoverableSpecForCommand(
            string commandId) => commandId == CommandId ? spec : null;
    }

    private sealed class RecoverableSuccessExecutor(
        PricingJobSpec spec,
        string deliverablePath) :
        IPricingJobExecutor,
        IRecoverablePricingJobExecutor
    {
        internal int RunCalls { get; private set; }

        public Task<PricingJobExecutionResult> RunAsync(
            PricingJobSpec requested,
            CancellationToken ct)
        {
            RunCalls++;
            Assert.Equal(spec, requested);
            return Task.FromResult(new PricingJobExecutionResult(
                new PricingJobProgress(
                    spec.JobId,
                    500,
                    500,
                    0,
                    PricingJobStatus.Completed),
                "uia",
                true,
                null,
                deliverablePath));
        }

        public PricingJobSpec? GetRecoverableSpec(
            PricingJobSpec proposed,
            string? commandId) => spec;

        public PricingJobSpec? GetRecoverableSpecForCommand(
            string commandId) => commandId == CommandId ? spec : null;
    }

    private sealed class RecordingPricedWorkbookPublisher(
        bool published,
        bool throwAfterFirstPublication) : IPricedWorkbookPublisher
    {
        internal int Calls { get; private set; }
        internal List<string> CommandIds { get; } = [];

        public Task<PricedWorkbookPublicationResult> PublishAsync(
            string commandId,
            string localWorkbookPath,
            CancellationToken ct)
        {
            Calls++;
            CommandIds.Add(commandId);
            if (throwAfterFirstPublication && Calls == 1)
                throw new OperationCanceledException(
                    "Simulated crash after durable local publication.");
            return Task.FromResult(new PricedWorkbookPublicationResult(
                published,
                published
                    ? PioneerRxPricedWorkbookPublicationCodes.Published
                    : PioneerRxPricedWorkbookPublicationCodes.PublicationFailed));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
