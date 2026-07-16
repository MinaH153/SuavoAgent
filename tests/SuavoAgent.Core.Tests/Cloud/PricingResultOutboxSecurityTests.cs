using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Cloud;

public sealed class PricingResultOutboxSecurityTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"suavo_pricing_outbox_security_{Guid.NewGuid():N}");
    private static readonly string ValidSignature =
        Convert.ToBase64String(new byte[64]);

    public PricingResultOutboxSecurityTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void AcceptedReceipt_IsExactlyIdempotentAndCannotBeRebound()
    {
        using var db = NewDb("accepted-idempotency.db");
        var entry = StageEmpty(db, "accepted-idempotency");
        var receipt = "{\"accepted\":true,\"jobId\":\"accepted-idempotency\",\"recorded\":0}";
        db.MarkPricingResultPayloadAccepted(
            entry.JobId,
            entry.PayloadSha256,
            0,
            "pricing_result_upload_accepted",
            receipt,
            RemoteCommandTrust.CommandV1KeyId,
            ValidSignature);

        db.MarkPricingResultPayloadAccepted(
            entry.JobId,
            entry.PayloadSha256,
            0,
            "pricing_result_upload_accepted",
            receipt,
            RemoteCommandTrust.CommandV1KeyId,
            ValidSignature);
        Assert.Throws<InvalidOperationException>(() =>
            db.MarkPricingResultPayloadAccepted(
                entry.JobId,
                entry.PayloadSha256,
                0,
                "pricing_result_upload_accepted",
                receipt + " ",
                RemoteCommandTrust.CommandV1KeyId,
                ValidSignature));
        Assert.Throws<InvalidOperationException>(() =>
            db.MarkPricingResultPayloadAccepted(
                entry.JobId,
                entry.PayloadSha256,
                0,
                "pricing_result_upload_accepted",
                receipt,
                RemoteCommandTrust.CommandV1KeyId,
                Convert.ToBase64String(Enumerable.Repeat((byte)1, 64).ToArray())));

        var retained = Assert.IsType<AgentStateDb.PricingResultOutboxEntry>(
            db.GetPricingResultOutbox(entry.JobId));
        Assert.Equal(receipt, retained.AcceptedReceiptJson);
        Assert.Equal(ValidSignature, retained.AcceptedResponseSignature);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AcceptedEvidence_FreezesRetriesAndFinalizationAfterFirstCommit(bool legacy)
    {
        var path = Path.Combine(_root, $"accepted-freeze-{legacy}.db");
        using var db = new AgentStateDb(path);
        var sourceId = Guid.NewGuid();
        var entry = legacy
            ? InsertLegacyEmpty(db, path, "accepted-freeze-legacy", sourceId)
            : StageEmpty(db, "accepted-freeze-v2", sourceId);
        var receipt = $"{{\"accepted\":true,\"jobId\":\"{entry.JobId}\",\"recorded\":0}}";
        db.MarkPricingResultPayloadAccepted(
            entry.JobId,
            entry.PayloadSha256,
            0,
            "pricing_result_upload_accepted",
            receipt,
            RemoteCommandTrust.CommandV1KeyId,
            ValidSignature);
        var table = legacy ? "pricing_result_outbox" : "pricing_result_outbox_v2";

        AssertSqliteRejected(path, table switch
        {
            "pricing_result_outbox" =>
                "UPDATE pricing_result_outbox SET attempt_count = attempt_count + 1 WHERE job_id = @job",
            _ =>
                "UPDATE pricing_result_outbox_v2 SET attempt_count = attempt_count + 1 WHERE job_id = @job",
        }, entry.JobId);
        AssertSqliteRejected(path, table switch
        {
            "pricing_result_outbox" =>
                "UPDATE pricing_result_outbox SET next_attempt_at = '2099-01-01T00:00:00Z' WHERE job_id = @job",
            _ =>
                "UPDATE pricing_result_outbox_v2 SET next_attempt_at = '2099-01-01T00:00:00Z' WHERE job_id = @job",
        }, entry.JobId);

        db.MarkPricingResultSourceFinalized(sourceId, entry.JobId, entry.PayloadSha256);
        AssertSqliteRejected(path, table switch
        {
            "pricing_result_outbox" =>
                "UPDATE pricing_result_outbox SET source_finalized_at = NULL WHERE job_id = @job",
            _ =>
                "UPDATE pricing_result_outbox_v2 SET source_finalized_at = NULL WHERE job_id = @job",
        }, entry.JobId);
        AssertSqliteRejected(path, table switch
        {
            "pricing_result_outbox" =>
                "UPDATE pricing_result_outbox SET source_finalized_at = '2099-01-01T00:00:00Z' WHERE job_id = @job",
            _ =>
                "UPDATE pricing_result_outbox_v2 SET source_finalized_at = '2099-01-01T00:00:00Z' WHERE job_id = @job",
        }, entry.JobId);
    }

    [Theory]
    [InlineData("digest")]
    [InlineData("command")]
    [InlineData("key")]
    [InlineData("signature")]
    public void V2Schema_RejectsMalformedEvidenceGrammar(string defect)
    {
        var path = Path.Combine(_root, $"schema-{defect}.db");
        using var db = new AgentStateDb(path);
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        var accepted = defect is "key" or "signature";
        command.CommandText = """
            INSERT INTO pricing_result_outbox_v2 (
                job_id, generation, command_id, payload_json, payload_sha256,
                item_count, execution_ok, state, attempt_count, next_attempt_at,
                created_at, accepted_at, accepted_code, accepted_recorded,
                accepted_receipt_json, accepted_receipt_sha256,
                accepted_response_key_id, accepted_response_signature
            ) VALUES (
                @job, 1, @command, '{}', @digest,
                0, 1, @state, 0, @now,
                @now, @accepted_at, @accepted_code, @accepted_recorded,
                @receipt, @receipt_digest, @key_id, @signature
            )
            """;
        command.Parameters.AddWithValue("@job", $"schema-{defect}");
        command.Parameters.AddWithValue(
            "@command", defect == "command" ? "../unsafe" : DBNull.Value);
        command.Parameters.AddWithValue(
            "@digest", defect == "digest" ? new string('g', 64) : new string('a', 64));
        command.Parameters.AddWithValue("@state", accepted ? "accepted" : "pending");
        command.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o"));
        command.Parameters.AddWithValue(
            "@accepted_at", accepted ? DateTimeOffset.UtcNow.ToString("o") : DBNull.Value);
        command.Parameters.AddWithValue(
            "@accepted_code", accepted ? "pricing_result_upload_accepted" : DBNull.Value);
        command.Parameters.AddWithValue("@accepted_recorded", accepted ? 0 : DBNull.Value);
        command.Parameters.AddWithValue(
            "@receipt", accepted ? "{}" : DBNull.Value);
        command.Parameters.AddWithValue(
            "@receipt_digest", accepted ? new string('b', 64) : DBNull.Value);
        command.Parameters.AddWithValue(
            "@key_id", accepted
                ? defect == "key" ? "attacker-key" : RemoteCommandTrust.CommandV1KeyId
                : DBNull.Value);
        command.Parameters.AddWithValue(
            "@signature", accepted
                ? defect == "signature" ? new string('!', 88) : ValidSignature
                : DBNull.Value);

        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
    }

    [Fact]
    public void TerminalReceipt_RejectsOrphanedJobAndDigestEvidence()
    {
        var path = Path.Combine(_root, "terminal-orphan.db");
        using var db = new AgentStateDb(path);
        var entry = StageEmpty(db, "terminal-real");
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO pricing_result_outbox_terminal_receipts (
                job_id, payload_sha256, reason_code, quarantined_at
            ) VALUES (
                @job, @digest, 'pricing_result_outbox_content_blocked', @now
            )
            """;
        command.Parameters.AddWithValue("@job", "terminal-wrong-job");
        command.Parameters.AddWithValue("@digest", entry.PayloadSha256);
        command.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o"));

        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
        command.Parameters["@job"].Value = entry.JobId;
        command.Parameters["@digest"].Value = new string('b', 64);
        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
    }

    [Fact]
    public void Version19Database_UpgradesWithoutMutatingLegacyEvidence()
    {
        var path = Path.Combine(_root, "upgrade-v19.db");
        const string jobId = "upgrade-v19";
        const string payload = "{\"legacy\":true}";
        var digest = Digest(payload);
        CreateVersion19Database(path, jobId, payload, digest);

        using var db = new AgentStateDb(path);
        var retained = Assert.IsType<AgentStateDb.PricingResultOutboxEntry>(
            db.GetPricingResultOutbox(jobId));
        Assert.True(retained.Legacy);
        Assert.Equal(payload, retained.PayloadJson);
        Assert.Equal(digest, retained.PayloadSha256);
        Assert.Null(retained.AcceptedResponseKeyId);
        Assert.Null(retained.AcceptedResponseSignature);

        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        Assert.Equal(1L, ScalarLong(connection,
            "SELECT count(*) FROM schema_migrations WHERE version = 20"));
        Assert.Equal(1L, ScalarLong(connection,
            "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'pricing_result_outbox_v2'"));
        Assert.Equal(2L, ScalarLong(connection,
            "SELECT count(*) FROM pragma_table_info('pricing_result_outbox') WHERE name IN ('accepted_response_key_id', 'accepted_response_signature')"));
    }

    private AgentStateDb NewDb(string name) =>
        new(Path.Combine(_root, name));

    private static AgentStateDb.PricingResultOutboxEntry StageEmpty(
        AgentStateDb db,
        string jobId,
        Guid? sourceId = null)
    {
        var payload = PricingJobCloudUploader.BuildPersistedPayloadEnvelope(
            jobId, null, PricingJobStatus.Completed, "sql", 0, 0, 0, [],
            "11111111-1111-4111-8111-111111111111",
            new string('a', 64));
        return db.StagePricingResultPayload(
            jobId, null, sourceId, payload.Json, 0, true);
    }

    private static AgentStateDb.PricingResultOutboxEntry InsertLegacyEmpty(
        AgentStateDb db,
        string path,
        string jobId,
        Guid sourceId)
    {
        var payload = PricingJobCloudUploader.BuildPersistedPayloadEnvelope(
            jobId, null, PricingJobStatus.Completed, "sql", 0, 0, 0, [],
            "11111111-1111-4111-8111-111111111111",
            new string('a', 64));
        var digest = Digest(payload.Json);
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO pricing_result_outbox (
                job_id, source_upload_id, payload_json, payload_sha256,
                item_count, execution_ok, state, attempt_count,
                next_attempt_at, created_at
            ) VALUES (
                @job, @source, @payload, @digest,
                0, 1, 'pending', 0, @now, @now
            )
            """;
        command.Parameters.AddWithValue("@job", jobId);
        command.Parameters.AddWithValue("@source", sourceId.ToString("D"));
        command.Parameters.AddWithValue("@payload", payload.Json);
        command.Parameters.AddWithValue("@digest", digest);
        command.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o"));
        Assert.Equal(1, command.ExecuteNonQuery());
        return Assert.IsType<AgentStateDb.PricingResultOutboxEntry>(
            db.GetPricingResultOutbox(jobId));
    }

    private static void AssertSqliteRejected(
        string path,
        string sql,
        string jobId)
    {
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@job", jobId);
        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
    }

    private static void CreateVersion19Database(
        string path,
        string jobId,
        string payload,
        string digest)
    {
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE schema_migrations (
                version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL,
                description TEXT NOT NULL
            );
            WITH RECURSIVE versions(value) AS (
                SELECT 1 UNION ALL SELECT value + 1 FROM versions WHERE value < 19
            )
            INSERT INTO schema_migrations(version, applied_at, description)
            SELECT value, @now, 'pre-v20' FROM versions;
            CREATE TABLE pricing_result_outbox (
                job_id TEXT PRIMARY KEY, command_id TEXT,
                source_upload_id TEXT UNIQUE, payload_json TEXT NOT NULL,
                payload_sha256 TEXT NOT NULL CHECK(length(payload_sha256) = 64),
                item_count INTEGER NOT NULL CHECK(item_count >= 0),
                execution_ok INTEGER NOT NULL CHECK(execution_ok IN (0, 1)),
                state TEXT NOT NULL CHECK(state IN ('pending', 'accepted')),
                attempt_count INTEGER NOT NULL DEFAULT 0 CHECK(attempt_count >= 0),
                next_attempt_at TEXT NOT NULL, created_at TEXT NOT NULL,
                accepted_at TEXT, accepted_code TEXT, accepted_recorded INTEGER,
                accepted_receipt_json TEXT, accepted_receipt_sha256 TEXT,
                source_finalized_at TEXT
            );
            CREATE UNIQUE INDEX idx_pricing_result_outbox_evidence_identity
                ON pricing_result_outbox(job_id, payload_sha256);
            CREATE TABLE pricing_result_outbox_quarantine (
                job_id TEXT NOT NULL, payload_sha256 TEXT NOT NULL,
                reason_code TEXT NOT NULL, quarantined_at TEXT NOT NULL,
                PRIMARY KEY(job_id, payload_sha256)
            );
            CREATE TABLE pricing_result_delivery_intents (
                job_id TEXT PRIMARY KEY, command_id TEXT,
                source_upload_id TEXT UNIQUE, source_mode TEXT NOT NULL,
                prepared_at TEXT NOT NULL, terminal_at TEXT
            );
            INSERT INTO pricing_result_outbox (
                job_id, payload_json, payload_sha256, item_count,
                execution_ok, state, attempt_count, next_attempt_at, created_at
            ) VALUES (
                @job, @payload, @digest, 0,
                0, 'pending', 0, @now, @now
            );
            """;
        command.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o"));
        command.Parameters.AddWithValue("@job", jobId);
        command.Parameters.AddWithValue("@payload", payload);
        command.Parameters.AddWithValue("@digest", digest);
        command.ExecuteNonQuery();
    }

    private static long ScalarLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
