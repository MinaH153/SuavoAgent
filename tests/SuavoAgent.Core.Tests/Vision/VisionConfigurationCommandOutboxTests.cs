using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Data.Sqlite;
using SuavoAgent.Contracts.Security;
using SuavoAgent.Contracts.Vision;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.State;
using SuavoAgent.Core.Vision;
using Xunit;

namespace SuavoAgent.Core.Tests.Vision;

public sealed class VisionConfigurationCommandOutboxTests : IDisposable
{
    private const string CommandId = "11111111-1111-4111-8111-111111111111";
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "suavo-vision-outbox-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_root, "state.db");

    public VisionConfigurationCommandOutboxTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Crash_after_durable_registration_recovers_without_command_redelivery()
    {
        var registry = new MemoryRegistryStore();
        using (var firstDb = new AgentStateDb(DatabasePath))
        {
            var first = CreateOutbox(firstDb, registry, (_, _, _, _, _) => Task.FromResult(true));
            var registered = first.RegisterVerified(Command(), Envelope());
            Assert.True(registered.Accepted, registered.Code);
            // Simulated process death: no cohort verification, registry apply, or ACK.
        }

        var ackCount = 0;
        using (var recoveredDb = new AgentStateDb(DatabasePath))
        {
            var recovered = CreateOutbox(
                recoveredDb,
                registry,
                (_, _, _, _, _) =>
                {
                    ackCount++;
                    return Task.FromResult(true);
                });

            await recovered.RetryPendingAsync(CancellationToken.None);

            Assert.NotNull(registry.Value);
            Assert.Equal(1, registry.WriteCount);
            Assert.Equal(1, ackCount);
            Assert.Empty(((IVisionConfigurationCommandLedger)recoveredDb)
                .GetPendingVisionConfigurations(10));
        }
    }

    [Fact]
    public async Task Ack_failure_retries_without_reapplying_registry_state()
    {
        var registry = new MemoryRegistryStore();
        using var db = new AgentStateDb(DatabasePath);
        var failedAckCount = 0;
        var first = CreateOutbox(
            db,
            registry,
            (_, _, _, _, _) =>
            {
                failedAckCount++;
                return Task.FromResult(false);
            });
        Assert.True(first.RegisterVerified(Command(), Envelope()).Accepted);

        await first.RetryPendingAsync(CancellationToken.None);

        var pendingAck = Assert.Single(((IVisionConfigurationCommandLedger)db)
            .GetPendingVisionConfigurations(10));
        Assert.Equal(VisionConfigurationOutboxState.PendingAck, pendingAck.State);
        Assert.Equal(1, registry.WriteCount);
        Assert.Equal(1, failedAckCount);

        var recoveredAckCount = 0;
        var recovered = CreateOutbox(
            db,
            registry,
            (_, _, _, _, _) =>
            {
                recoveredAckCount++;
                return Task.FromResult(true);
            });
        await recovered.RetryPendingAsync(CancellationToken.None);

        Assert.Equal(1, registry.WriteCount);
        Assert.Equal(1, recoveredAckCount);
        Assert.Empty(((IVisionConfigurationCommandLedger)db)
            .GetPendingVisionConfigurations(10));
    }

    [Fact]
    public async Task Crash_after_registry_apply_before_ledger_transition_is_idempotent()
    {
        var registry = new MemoryRegistryStore();
        using var db = new AgentStateDb(DatabasePath);
        var durable = (IVisionConfigurationCommandLedger)db;
        var crashingLedger = new FailPendingAckOnceLedger(durable);
        var first = CreateOutbox(
            crashingLedger,
            registry,
            (_, _, _, _, _) => Task.FromResult(true));
        Assert.True(first.RegisterVerified(Command(), Envelope()).Accepted);

        await first.RetryPendingAsync(CancellationToken.None);

        Assert.Equal(1, registry.WriteCount);
        Assert.Equal(VisionConfigurationOutboxState.PendingApply,
            Assert.Single(durable.GetPendingVisionConfigurations(10)).State);

        var ackCount = 0;
        var recovered = CreateOutbox(
            durable,
            registry,
            (_, _, _, _, _) =>
            {
                ackCount++;
                return Task.FromResult(true);
            });
        await recovered.RetryPendingAsync(CancellationToken.None);

        Assert.Equal(1, registry.WriteCount);
        Assert.Equal(1, ackCount);
        Assert.Empty(durable.GetPendingVisionConfigurations(10));
    }

    [Fact]
    public void Structural_failure_without_command_id_survives_restart()
    {
        using (var firstDb = new AgentStateDb(DatabasePath))
        {
            var outbox = CreateOutbox(
                firstDb,
                new MemoryRegistryStore(),
                (_, _, _, _, _) => Task.FromResult(true));
            outbox.RecordStructuralFailure(
                Envelope(),
                null,
                "vision_command_id_invalid");
        }

        using var reopened = new AgentStateDb(DatabasePath);
        var latest = ((IVisionConfigurationCommandLedger)reopened)
            .GetLatestVisionConfigurationStructuralFailure();
        Assert.NotNull(latest);
        Assert.Equal("vision_command_id_invalid", latest.Value.Code);
    }

    [Fact]
    public async Task Missing_preinstalled_cohort_never_writes_registry_and_negative_ack_retries()
    {
        var registry = new MemoryRegistryStore();
        var attempts = 0;
        Func<string, bool, object?, string?, CancellationToken, Task<bool>> ack =
            (_, succeeded, result, error, _) =>
            {
                attempts++;
                Assert.False(succeeded);
                Assert.NotNull(result);
                Assert.Equal("vision_native_cohort_maintenance_required", error);
                return Task.FromResult(attempts > 1);
            };
        using (var firstDb = new AgentStateDb(DatabasePath))
        {
            var outbox = CreateOutbox(
                (IVisionConfigurationCommandLedger)firstDb,
                registry,
                ack,
                verifyPreinstalledCohort: _ => false);
            Assert.True(outbox.RegisterVerified(EnabledOcrCommand(), Envelope()).Accepted);
            await outbox.RetryPendingAsync(CancellationToken.None);

            var pending = Assert.Single(((IVisionConfigurationCommandLedger)firstDb)
                .GetPendingVisionConfigurations(10));
            Assert.Equal(VisionConfigurationOutboxState.PendingAck, pending.State);
            Assert.False(pending.ApplySucceeded);
            Assert.Null(pending.Generation);
            Assert.Equal(0, registry.WriteCount);
        }

        using (var recoveredDb = new AgentStateDb(DatabasePath))
        {
            var recovered = CreateOutbox(
                (IVisionConfigurationCommandLedger)recoveredDb,
                registry,
                ack,
                verifyPreinstalledCohort: _ => throw new InvalidOperationException(
                    "Pending negative ACK must not re-enter cohort verification"));
            await recovered.RetryPendingAsync(CancellationToken.None);
            Assert.Empty(((IVisionConfigurationCommandLedger)recoveredDb)
                .GetPendingVisionConfigurations(10));
        }

        Assert.Equal(2, attempts);
        Assert.Equal(0, registry.WriteCount);
    }

    [Fact]
    public void Existing_outbox_schema_migrates_apply_outcome_without_data_loss()
    {
        using (var connection = new SqliteConnection($"Data Source={DatabasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE vision_configuration_commands (
                    command_id TEXT PRIMARY KEY,
                    config_digest TEXT NOT NULL,
                    options_document TEXT NOT NULL,
                    bundle_url TEXT,
                    bundle_sha256 TEXT,
                    envelope_nonce TEXT NOT NULL UNIQUE,
                    envelope_binding TEXT NOT NULL,
                    state TEXT NOT NULL,
                    generation INTEGER,
                    result_code TEXT,
                    registered_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );
                """;
            command.ExecuteNonQuery();
        }

        using var db = new AgentStateDb(DatabasePath);
        var outbox = CreateOutbox(
            db,
            new MemoryRegistryStore(),
            (_, _, _, _, _) => Task.FromResult(true));

        Assert.True(outbox.RegisterVerified(Command(), Envelope()).Accepted);
        var pending = Assert.Single(((IVisionConfigurationCommandLedger)db)
            .GetPendingVisionConfigurations(10));
        Assert.True(pending.ApplySucceeded);
    }

    private VisionConfigurationCommandOutbox CreateOutbox(
        AgentStateDb db,
        MemoryRegistryStore registry,
        Func<string, bool, object?, string?, CancellationToken, Task<bool>> ack) =>
        CreateOutbox((IVisionConfigurationCommandLedger)db, registry, ack);

    private VisionConfigurationCommandOutbox CreateOutbox(
        IVisionConfigurationCommandLedger ledger,
        MemoryRegistryStore registry,
        Func<string, bool, object?, string?, CancellationToken, Task<bool>> ack,
        Func<VisionOptionsSnapshot, bool>? verifyPreinstalledCohort = null)
    {
        var effective = VisionConfigurationRegistry.Load(registry, _root);
        var status = new VisionConfigurationStatusProvider(effective, registry, _root);
        var coordinator = new VisionConfigurationCoordinator(
            registry,
            _root,
            () => new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.Zero));
        return new(
            ledger,
            coordinator,
            status,
            _root,
            verifyPreinstalledCohort ?? (_ => true),
            ack,
            NullLogger.Instance,
            () => new DateTimeOffset(2026, 7, 11, 11, 0, 0, TimeSpan.Zero));
    }

    private VisionConfigurationCommand Command()
    {
        var cohorts = Path.Combine(_root, "vision", "cohorts");
        return new(
            CommandId,
            false,
            false,
            null,
            null,
            null,
            null,
            cohorts,
            Path.Combine(cohorts, "tessdata"),
            "eng",
            50,
            VisionOptionsSnapshot.DisabledDefault());
    }

    private VisionConfigurationCommand EnabledOcrCommand()
    {
        var bundleSha = new string('a', 64);
        var cohortRoot = Path.Combine(_root, "vision", "cohorts", bundleSha);
        var defaults = VisionOptionsSnapshot.DisabledDefault();
        var options = defaults with
        {
            Enabled = true,
            Tesseract = defaults.Tesseract with
            {
                Enabled = true,
                CohortId = "reviewed-test-cohort",
                BundleSha256 = bundleSha,
                ManifestSha256 = new string('b', 64),
                NativeLibraryPath = cohortRoot,
                TessdataPath = Path.Combine(cohortRoot, "tessdata"),
            },
        };
        return new(
            CommandId,
            true,
            true,
            "https://assets.example/reviewed.nupkg",
            bundleSha,
            options.Tesseract.CohortId,
            options.Tesseract.ManifestSha256,
            cohortRoot,
            Path.Combine(cohortRoot, "tessdata"),
            "eng",
            50,
            options);
    }

    private static SignedCommand Envelope() => new(
        "set_vision_config",
        "agent-test",
        "machine-test",
        "2026-07-11T11:00:00.0000000Z",
        "nonce-test",
        "test-key",
        Convert.ToBase64String(new byte[64]),
        new string('a', 64));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class MemoryRegistryStore : IVisionConfigurationStore
    {
        public string? Value { get; private set; }
        public int WriteCount { get; private set; }
        public VisionRegistryReadResult Read() => Value is null
            ? new(VisionRegistryReadStatus.Missing, "vision_registry_state_missing")
            : new(VisionRegistryReadStatus.Present, "present", Value);
        public void Write(string value)
        {
            WriteCount++;
            Value = value;
        }
    }

    private sealed class FailPendingAckOnceLedger(IVisionConfigurationCommandLedger inner)
        : IVisionConfigurationCommandLedger
    {
        private int _failed;
        public VisionConfigurationOutboxRegisterResult RegisterVisionConfiguration(
            VisionConfigurationOutboxRegistration registration) =>
            inner.RegisterVisionConfiguration(registration);
        public IReadOnlyList<VisionConfigurationOutboxItem> GetPendingVisionConfigurations(
            int maximum) => inner.GetPendingVisionConfigurations(maximum);
        public bool MarkVisionConfigurationPendingAck(
            string commandId,
            string configDigest,
            long? generation,
            bool applySucceeded,
            string resultCode) =>
            Interlocked.Exchange(ref _failed, 1) == 0
                ? false
                : inner.MarkVisionConfigurationPendingAck(
                    commandId,
                    configDigest,
                    generation,
                    applySucceeded,
                    resultCode);
        public bool MarkVisionConfigurationAcked(string commandId, string configDigest) =>
            inner.MarkVisionConfigurationAcked(commandId, configDigest);
        public void RecordVisionConfigurationStructuralFailure(
            string envelopeBinding,
            string? commandId,
            string code) => inner.RecordVisionConfigurationStructuralFailure(
            envelopeBinding,
            commandId,
            code);
        public (string Code, DateTimeOffset RecordedAt)?
            GetLatestVisionConfigurationStructuralFailure() =>
            inner.GetLatestVisionConfigurationStructuralFailure();
    }
}
