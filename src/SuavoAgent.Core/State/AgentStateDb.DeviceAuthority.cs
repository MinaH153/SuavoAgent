using System.Text.Json;
using Microsoft.Data.Sqlite;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.Workers;

namespace SuavoAgent.Core.State;

public sealed partial class AgentStateDb
{
    internal sealed record PersistedPomDeviceReceipt(
        SignedDeviceReceipt<PomActivationDeviceReceipt> Signed,
        string? SourceBindingId,
        bool Accepted);

    internal sealed record PersistedRxDeviceReceipt(
        SignedDeviceReceipt<RxSourceDeviceReceipt> Signed,
        bool Accepted);

    internal sealed record PersistedSeedApplicationReceipt(
        SignedDeviceReceipt<SeedApplicationDeviceReceipt> Signed,
        bool Accepted);

    internal sealed record CloudLearnedSourceBinding(
        string SourceBindingId,
        string PomId,
        string SessionId,
        string ModelDigest,
        string TemplateDigest);

    internal PersistedPomDeviceReceipt GetOrCreatePomDeviceReceipt(
        PomApprovalCommand command,
        PomApprovalLedgerResult terminal,
        PomApprovalLedgerRow ledger,
        AgentOptions options,
        IDeviceAuthoritySigner signer)
    {
        if (!PomApprovalCommandContract.IsSafeResultCode(terminal.OutcomeCode) ||
            string.IsNullOrWhiteSpace(ledger.CompletedAt) ||
            string.IsNullOrWhiteSpace(options.AgentId) ||
            string.IsNullOrWhiteSpace(options.PharmacyId) ||
            string.IsNullOrWhiteSpace(options.MachineFingerprint))
            throw new InvalidOperationException("POM device receipt identity is incomplete.");

        lock (_connLock)
        {
            using var transaction = _conn.BeginTransaction();
            var existing = ReadPomDeviceReceipt(transaction, command.CommandId);
            if (existing is not null)
            {
                transaction.Commit();
                if (!string.Equals(
                        existing.Signed.Receipt.CommandPayloadDigest,
                        command.PayloadDigest,
                        StringComparison.Ordinal) ||
                    !string.Equals(existing.Signed.KeyId, signer.KeyId, StringComparison.Ordinal))
                    throw new InvalidOperationException("POM device receipt replay conflict.");
                return existing;
            }

            var counter = NextDeviceCounter(transaction, "pom_activation");
            var completed = DateTimeOffset.Parse(ledger.CompletedAt)
                .ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'");
            var receipt = new PomActivationDeviceReceipt(
                1,
                command.CommandId,
                options.AgentId,
                options.PharmacyId,
                options.MachineFingerprint,
                command.PomId,
                command.SessionId,
                command.ApprovedModelDigest,
                command.ApprovedTemplateDigest,
                command.ApprovedBy,
                terminal.OutcomeCode,
                counter,
                completed,
                command.PayloadDigest);
            var signed = signer.Sign(receipt);
            using var insert = CreateCommand(transaction, """
                INSERT INTO device_pom_activation_receipts
                    (command_id, payload_digest, key_id, local_counter,
                     receipt_json, signature, canonical_digest, committed_at)
                VALUES
                    (@command, @payload, @key, @counter,
                     @receipt, @signature, @canonical, @committed)
                """);
            insert.Parameters.AddWithValue("@command", command.CommandId);
            insert.Parameters.AddWithValue("@payload", command.PayloadDigest);
            insert.Parameters.AddWithValue("@key", signed.KeyId);
            insert.Parameters.AddWithValue("@counter", counter);
            insert.Parameters.AddWithValue(
                "@receipt",
                JsonSerializer.Serialize(receipt, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            insert.Parameters.AddWithValue("@signature", signed.Signature);
            insert.Parameters.AddWithValue("@canonical", signed.CanonicalDigest);
            insert.Parameters.AddWithValue("@committed", DateTimeOffset.UtcNow.ToString("o"));
            insert.ExecuteNonQuery();
            transaction.Commit();
            return new(signed, null, false);
        }
    }

    internal void MarkPomDeviceReceiptAccepted(
        string commandId,
        string? sourceBindingId)
    {
        lock (_connLock)
        {
            using var command = _conn.CreateCommand();
            command.CommandText = """
                UPDATE device_pom_activation_receipts
                   SET source_binding_id = COALESCE(source_binding_id, @binding),
                       accepted_at = COALESCE(accepted_at, @accepted)
                 WHERE command_id = @command
                   AND (source_binding_id IS NULL
                     OR source_binding_id IS @binding)
                """;
            command.Parameters.AddWithValue("@binding", (object?)sourceBindingId ?? DBNull.Value);
            command.Parameters.AddWithValue("@accepted", DateTimeOffset.UtcNow.ToString("o"));
            command.Parameters.AddWithValue("@command", commandId);
            if (command.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("POM source binding receipt conflict.");
        }
    }

    internal CloudLearnedSourceBinding? GetCloudLearnedSourceBinding(
        ActivePmsAdapterBinding localBinding)
    {
        lock (_connLock)
        {
            using var command = _conn.CreateCommand();
            command.CommandText = """
                SELECT receipt_json, source_binding_id
                  FROM device_pom_activation_receipts
                 WHERE source_binding_id IS NOT NULL
                   AND accepted_at IS NOT NULL
                 ORDER BY local_counter DESC
                 LIMIT 1
                """;
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            var receipt = JsonSerializer.Deserialize<PomActivationDeviceReceipt>(
                reader.GetString(0),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (receipt is null ||
                receipt.ResultCode is not (
                    "pom_approval_activated" or "pom_approval_already_active") ||
                !string.Equals(receipt.SessionId, localBinding.SessionId, StringComparison.Ordinal) ||
                !string.Equals(
                    receipt.ApprovedModelDigest,
                    localBinding.ModelDigest,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    receipt.ApprovedTemplateDigest,
                    localBinding.TemplateDigest,
                    StringComparison.Ordinal))
                return null;
            return new(
                reader.GetString(1),
                receipt.PomId,
                receipt.SessionId,
                receipt.ApprovedModelDigest,
                receipt.ApprovedTemplateDigest);
        }
    }

    internal PersistedRxDeviceReceipt GetOrCreateRxDeviceReceipt(
        string batchDigest,
        CloudLearnedSourceBinding binding,
        AgentOptions options,
        IDeviceAuthoritySigner signer)
    {
        lock (_connLock)
        {
            using var transaction = _conn.BeginTransaction();
            var existing = ReadRxDeviceReceipt(transaction, batchDigest);
            if (existing is not null)
            {
                transaction.Commit();
                if (!string.Equals(existing.Signed.KeyId, signer.KeyId, StringComparison.Ordinal))
                    throw new InvalidOperationException("Rx source receipt key conflict.");
                return existing;
            }

            if (string.IsNullOrWhiteSpace(options.AgentId) ||
                string.IsNullOrWhiteSpace(options.PharmacyId) ||
                string.IsNullOrWhiteSpace(options.MachineFingerprint))
                throw new InvalidOperationException("Rx source device identity is incomplete.");
            var counter = NextDeviceCounter(transaction, "rx_source");
            var receipt = new RxSourceDeviceReceipt(
                1,
                options.AgentId,
                options.PharmacyId,
                options.MachineFingerprint,
                batchDigest,
                "learned",
                binding.SourceBindingId,
                "learned-approved",
                $"learned.template.{binding.TemplateDigest}",
                binding.PomId,
                binding.SessionId,
                binding.ModelDigest,
                binding.TemplateDigest,
                counter,
                DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'"));
            var signed = signer.Sign(receipt);
            using var insert = CreateCommand(transaction, """
                INSERT INTO device_rx_source_receipts
                    (batch_digest, key_id, local_counter, receipt_json,
                     signature, canonical_digest, committed_at)
                VALUES
                    (@batch, @key, @counter, @receipt,
                     @signature, @canonical, @committed)
                """);
            insert.Parameters.AddWithValue("@batch", batchDigest);
            insert.Parameters.AddWithValue("@key", signed.KeyId);
            insert.Parameters.AddWithValue("@counter", counter);
            insert.Parameters.AddWithValue(
                "@receipt",
                JsonSerializer.Serialize(receipt, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            insert.Parameters.AddWithValue("@signature", signed.Signature);
            insert.Parameters.AddWithValue("@canonical", signed.CanonicalDigest);
            insert.Parameters.AddWithValue("@committed", DateTimeOffset.UtcNow.ToString("o"));
            insert.ExecuteNonQuery();
            transaction.Commit();
            return new(signed, false);
        }
    }

    internal void MarkRxDeviceReceiptAccepted(string batchDigest)
    {
        lock (_connLock)
        {
            using var command = _conn.CreateCommand();
            command.CommandText = """
                UPDATE device_rx_source_receipts
                   SET accepted_at = COALESCE(accepted_at, @accepted)
                 WHERE batch_digest = @batch
                """;
            command.Parameters.AddWithValue("@accepted", DateTimeOffset.UtcNow.ToString("o"));
            command.Parameters.AddWithValue("@batch", batchDigest);
            if (command.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("Rx source receipt is missing.");
        }
    }

    internal PersistedSeedApplicationReceipt GetOrCreateSeedApplicationReceipt(
        SeedResponse response,
        AgentOptions options,
        string sessionId,
        int correlationsApplied,
        int correlationsSkipped,
        IDeviceAuthoritySigner signer)
    {
        if (!Guid.TryParseExact(response.CommandId, "D", out _) ||
            string.IsNullOrWhiteSpace(response.DeviceKeyId) ||
            string.IsNullOrWhiteSpace(response.SourceManifestDigest) ||
            !string.Equals(response.DeviceKeyId, signer.KeyId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(options.AgentId) ||
            string.IsNullOrWhiteSpace(options.PharmacyId) ||
            string.IsNullOrWhiteSpace(sessionId) ||
            correlationsApplied < 0 || correlationsSkipped < 0)
            throw new InvalidOperationException(
                "Seed application receipt identity is incomplete.");

        lock (_connLock)
        {
            using var transaction = _conn.BeginTransaction();
            var existing = ReadSeedApplicationReceipt(transaction, response.CommandId!);
            if (existing is not null)
            {
                transaction.Commit();
                var existingReceipt = existing.Signed.Receipt;
                if (!string.Equals(existing.Signed.KeyId, signer.KeyId, StringComparison.Ordinal) ||
                    !string.Equals(existingReceipt.SeedDigest, response.SeedDigest, StringComparison.Ordinal) ||
                    existingReceipt.SeedVersion != response.SeedVersion ||
                    !string.Equals(existingReceipt.Phase, response.Phase, StringComparison.Ordinal) ||
                    !string.Equals(
                        existingReceipt.SourceManifestDigest,
                        response.SourceManifestDigest,
                        StringComparison.Ordinal) ||
                    !string.Equals(existingReceipt.SessionId, sessionId, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "Seed application receipt replay conflict.");
                return existing;
            }

            using var next = CreateCommand(transaction, """
                SELECT COALESCE(MAX(local_counter), 0) + 1
                  FROM device_seed_application_receipts
                """);
            var counter = Convert.ToInt64(next.ExecuteScalar());
            var receipt = new SeedApplicationDeviceReceipt(
                1,
                response.CommandId!,
                options.AgentId,
                options.PharmacyId,
                response.DeviceKeyId!,
                response.SeedDigest,
                response.SeedVersion,
                response.Phase,
                response.SourceManifestDigest!,
                sessionId,
                DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'"),
                correlationsApplied,
                correlationsSkipped,
                counter);
            var signed = signer.Sign(receipt);
            using var insert = CreateCommand(transaction, """
                INSERT INTO device_seed_application_receipts (
                    command_id, seed_digest, seed_version, phase,
                    source_manifest_digest, session_id, key_id, local_counter,
                    receipt_json, signature, canonical_digest, committed_at
                ) VALUES (
                    @command, @seed, @version, @phase,
                    @source, @session, @key, @counter,
                    @receipt, @signature, @canonical, @committed
                )
                """);
            insert.Parameters.AddWithValue("@command", response.CommandId);
            insert.Parameters.AddWithValue("@seed", response.SeedDigest);
            insert.Parameters.AddWithValue("@version", response.SeedVersion);
            insert.Parameters.AddWithValue("@phase", response.Phase);
            insert.Parameters.AddWithValue("@source", response.SourceManifestDigest);
            insert.Parameters.AddWithValue("@session", sessionId);
            insert.Parameters.AddWithValue("@key", signed.KeyId);
            insert.Parameters.AddWithValue("@counter", counter);
            insert.Parameters.AddWithValue(
                "@receipt",
                JsonSerializer.Serialize(
                    receipt,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            insert.Parameters.AddWithValue("@signature", signed.Signature);
            insert.Parameters.AddWithValue("@canonical", signed.CanonicalDigest);
            insert.Parameters.AddWithValue("@committed", DateTimeOffset.UtcNow.ToString("o"));
            insert.ExecuteNonQuery();
            transaction.Commit();
            return new(signed, false);
        }
    }

    internal PersistedSeedApplicationReceipt? GetSeedApplicationReceipt(
        string seedDigest)
    {
        lock (_connLock)
        {
            using var command = _conn.CreateCommand();
            command.CommandText = """
                SELECT key_id, receipt_json, signature, canonical_digest, accepted_at
                  FROM device_seed_application_receipts
                 WHERE seed_digest = @seed
                 ORDER BY local_counter DESC
                 LIMIT 1
                """;
            command.Parameters.AddWithValue("@seed", seedDigest);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            var receipt = JsonSerializer.Deserialize<SeedApplicationDeviceReceipt>(
                reader.GetString(1),
                new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidOperationException(
                    "Stored seed application receipt is invalid.");
            return new(
                new(receipt, reader.GetString(0), reader.GetString(2), reader.GetString(3)),
                !reader.IsDBNull(4));
        }
    }

    internal void MarkSeedApplicationReceiptAccepted(string commandId)
    {
        lock (_connLock)
        {
            using var command = _conn.CreateCommand();
            command.CommandText = """
                UPDATE device_seed_application_receipts
                   SET accepted_at = COALESCE(accepted_at, @accepted)
                 WHERE command_id = @command
                """;
            command.Parameters.AddWithValue("@accepted", DateTimeOffset.UtcNow.ToString("o"));
            command.Parameters.AddWithValue("@command", commandId);
            if (command.ExecuteNonQuery() != 1)
                throw new InvalidOperationException(
                    "Seed application receipt is missing.");
        }
    }

    private long NextDeviceCounter(SqliteTransaction transaction, string kind)
    {
        using var update = CreateCommand(transaction, """
            UPDATE device_authority_counters
               SET counter = counter + 1
             WHERE kind = @kind
            RETURNING counter
            """);
        update.Parameters.AddWithValue("@kind", kind);
        return Convert.ToInt64(update.ExecuteScalar()
            ?? throw new InvalidOperationException("Device authority counter is missing."));
    }

    private PersistedPomDeviceReceipt? ReadPomDeviceReceipt(
        SqliteTransaction transaction,
        string commandId)
    {
        using var command = CreateCommand(transaction, """
            SELECT key_id, receipt_json, signature, canonical_digest,
                   source_binding_id, accepted_at
              FROM device_pom_activation_receipts
             WHERE command_id = @command
            """);
        command.Parameters.AddWithValue("@command", commandId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var receipt = JsonSerializer.Deserialize<PomActivationDeviceReceipt>(
            reader.GetString(1), new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("Stored POM device receipt is invalid.");
        return new(
            new(receipt, reader.GetString(0), reader.GetString(2), reader.GetString(3)),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            !reader.IsDBNull(5));
    }

    private PersistedRxDeviceReceipt? ReadRxDeviceReceipt(
        SqliteTransaction transaction,
        string batchDigest)
    {
        using var command = CreateCommand(transaction, """
            SELECT key_id, receipt_json, signature, canonical_digest, accepted_at
              FROM device_rx_source_receipts
             WHERE batch_digest = @batch
            """);
        command.Parameters.AddWithValue("@batch", batchDigest);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var receipt = JsonSerializer.Deserialize<RxSourceDeviceReceipt>(
            reader.GetString(1), new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("Stored Rx source receipt is invalid.");
        return new(
            new(receipt, reader.GetString(0), reader.GetString(2), reader.GetString(3)),
            !reader.IsDBNull(4));
    }

    private PersistedSeedApplicationReceipt? ReadSeedApplicationReceipt(
        SqliteTransaction transaction,
        string commandId)
    {
        using var command = CreateCommand(transaction, """
            SELECT key_id, receipt_json, signature, canonical_digest, accepted_at
              FROM device_seed_application_receipts
             WHERE command_id = @command
            """);
        command.Parameters.AddWithValue("@command", commandId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var receipt = JsonSerializer.Deserialize<SeedApplicationDeviceReceipt>(
            reader.GetString(1),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException(
                "Stored seed application receipt is invalid.");
        return new(
            new(receipt, reader.GetString(0), reader.GetString(2), reader.GetString(3)),
            !reader.IsDBNull(4));
    }
}
