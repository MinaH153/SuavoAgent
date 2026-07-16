using System.Globalization;
using Microsoft.Data.Sqlite;
using SuavoAgent.Contracts.Vision;
using SuavoAgent.Core.Vision;

namespace SuavoAgent.Core.State;

public sealed partial class AgentStateDb : IVisionConfigurationCommandLedger
{
    VisionConfigurationOutboxRegisterResult
        IVisionConfigurationCommandLedger.RegisterVisionConfiguration(
            VisionConfigurationOutboxRegistration registration)
    {
        ValidateRegistration(registration);
        lock (_connLock)
        {
            using var transaction = _conn.BeginTransaction(
                System.Data.IsolationLevel.Serializable);
            var existing = ReadVisionConfiguration(
                registration.CommandId,
                transaction);
            if (existing is not null)
            {
                var same = string.Equals(
                               existing.ConfigDigest,
                               registration.ConfigDigest,
                               StringComparison.Ordinal) &&
                           string.Equals(
                               existing.OptionsDocument,
                               registration.OptionsDocument,
                               StringComparison.Ordinal) &&
                           string.Equals(
                               existing.BundleUrl,
                               registration.BundleUrl,
                               StringComparison.Ordinal) &&
                           string.Equals(
                               existing.BundleSha256,
                               registration.BundleSha256,
                               StringComparison.Ordinal);
                transaction.Commit();
                return same
                    ? new(true, true, "idempotent", existing)
                    : new(false, false, "vision_command_replay_conflict", existing);
            }

            using (var nonce = CreateCommand(
                       transaction,
                       "SELECT command_id FROM vision_configuration_commands " +
                       "WHERE envelope_nonce = @nonce LIMIT 1"))
            {
                nonce.Parameters.AddWithValue("@nonce", registration.EnvelopeNonce);
                if (nonce.ExecuteScalar() is not null)
                {
                    transaction.Commit();
                    return new(false, false, "vision_envelope_nonce_conflict");
                }
            }

            using (var insert = CreateCommand(
                       transaction,
                       """
                       INSERT INTO vision_configuration_commands
                           (command_id, config_digest, options_document, bundle_url,
                            bundle_sha256, envelope_nonce, envelope_binding, state,
                            apply_succeeded, generation, result_code, registered_at, updated_at)
                       VALUES
                           (@command, @digest, @options, @url, @bundle, @nonce,
                            @binding, 'pending_apply', 1, NULL, NULL, @registered, @updated)
                       """))
            {
                insert.Parameters.AddWithValue("@command", registration.CommandId);
                insert.Parameters.AddWithValue("@digest", registration.ConfigDigest);
                insert.Parameters.AddWithValue("@options", registration.OptionsDocument);
                insert.Parameters.AddWithValue("@url", (object?)registration.BundleUrl ?? DBNull.Value);
                insert.Parameters.AddWithValue("@bundle", (object?)registration.BundleSha256 ?? DBNull.Value);
                insert.Parameters.AddWithValue("@nonce", registration.EnvelopeNonce);
                insert.Parameters.AddWithValue("@binding", registration.EnvelopeBinding);
                insert.Parameters.AddWithValue("@registered", registration.RegisteredAt.ToString("O"));
                insert.Parameters.AddWithValue("@updated", registration.RegisteredAt.ToString("O"));
                insert.ExecuteNonQuery();
            }
            transaction.Commit();
            return new(
                true,
                false,
                "registered",
                new(
                    registration.CommandId,
                    registration.ConfigDigest,
                    registration.OptionsDocument,
                    registration.BundleUrl,
                    registration.BundleSha256,
                    registration.EnvelopeNonce,
                    registration.EnvelopeBinding,
                    VisionConfigurationOutboxState.PendingApply,
                    true,
                    null,
                    null,
                    registration.RegisteredAt,
                    registration.RegisteredAt));
        }
    }

    IReadOnlyList<VisionConfigurationOutboxItem>
        IVisionConfigurationCommandLedger.GetPendingVisionConfigurations(int maximum)
    {
        if (maximum is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(maximum));
        lock (_connLock)
        {
            using var command = _conn.CreateCommand();
            command.CommandText = """
                SELECT command_id, config_digest, options_document, bundle_url,
                       bundle_sha256, envelope_nonce, envelope_binding, state,
                       apply_succeeded, generation, result_code, registered_at, updated_at
                  FROM vision_configuration_commands
                 WHERE state IN ('pending_apply','pending_ack')
                 ORDER BY registered_at ASC
                 LIMIT @maximum
                """;
            command.Parameters.AddWithValue("@maximum", maximum);
            var results = new List<VisionConfigurationOutboxItem>();
            using var reader = command.ExecuteReader();
            while (reader.Read()) results.Add(ReadVisionConfiguration(reader));
            return results;
        }
    }

    bool IVisionConfigurationCommandLedger.MarkVisionConfigurationPendingAck(
        string commandId,
        string configDigest,
        long? generation,
        bool applySucceeded,
        string resultCode)
    {
        if (applySucceeded && generation is null or < 1 ||
            !applySucceeded && generation is not null)
            throw new ArgumentOutOfRangeException(nameof(generation));
        lock (_connLock)
        {
            using var command = _conn.CreateCommand();
            command.CommandText = """
                UPDATE vision_configuration_commands
                   SET state = 'pending_ack', generation = @generation,
                       apply_succeeded = @succeeded, result_code = @result,
                       updated_at = @updated
                 WHERE command_id = @command AND config_digest = @digest
                   AND state = 'pending_apply'
                """;
            command.Parameters.AddWithValue("@generation", (object?)generation ?? DBNull.Value);
            command.Parameters.AddWithValue("@succeeded", applySucceeded ? 1 : 0);
            command.Parameters.AddWithValue("@result", resultCode);
            command.Parameters.AddWithValue("@updated", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("@command", commandId);
            command.Parameters.AddWithValue("@digest", configDigest);
            return command.ExecuteNonQuery() == 1;
        }
    }

    bool IVisionConfigurationCommandLedger.MarkVisionConfigurationAcked(
        string commandId,
        string configDigest)
    {
        lock (_connLock)
        {
            using var command = _conn.CreateCommand();
            command.CommandText = """
                UPDATE vision_configuration_commands
                   SET state = 'acked', updated_at = @updated
                 WHERE command_id = @command AND config_digest = @digest
                   AND state = 'pending_ack'
                """;
            command.Parameters.AddWithValue("@updated", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("@command", commandId);
            command.Parameters.AddWithValue("@digest", configDigest);
            return command.ExecuteNonQuery() == 1;
        }
    }

    void IVisionConfigurationCommandLedger.RecordVisionConfigurationStructuralFailure(
        string envelopeBinding,
        string? commandId,
        string code)
    {
        if (!VisionOptionsSnapshot.IsLowerHexSha256(envelopeBinding) ||
            string.IsNullOrWhiteSpace(code) || code.Length > 128 || code.Any(char.IsControl) ||
            commandId is { Length: > 36 })
            throw new ArgumentException("Vision structural failure is invalid.");
        lock (_connLock)
        {
            using var command = _conn.CreateCommand();
            command.CommandText = """
                INSERT INTO vision_configuration_failures
                    (envelope_binding, command_id, code, recorded_at)
                VALUES (@binding, @command, @code, @recorded)
                ON CONFLICT (envelope_binding, code) DO NOTHING
                """;
            command.Parameters.AddWithValue("@binding", envelopeBinding);
            command.Parameters.AddWithValue("@command", (object?)commandId ?? DBNull.Value);
            command.Parameters.AddWithValue("@code", code);
            command.Parameters.AddWithValue("@recorded", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
    }

    (string Code, DateTimeOffset RecordedAt)?
        IVisionConfigurationCommandLedger.GetLatestVisionConfigurationStructuralFailure()
    {
        lock (_connLock)
        {
            using var command = _conn.CreateCommand();
            command.CommandText = """
                SELECT code, recorded_at
                  FROM vision_configuration_failures
                 ORDER BY id DESC
                 LIMIT 1
                """;
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            return (
                reader.GetString(0),
                DateTimeOffset.Parse(
                    reader.GetString(1),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind));
        }
    }

    private VisionConfigurationOutboxItem? ReadVisionConfiguration(
        string commandId,
        SqliteTransaction transaction)
    {
        using var command = CreateCommand(
            transaction,
            """
            SELECT command_id, config_digest, options_document, bundle_url,
                   bundle_sha256, envelope_nonce, envelope_binding, state,
                   apply_succeeded, generation, result_code, registered_at, updated_at
              FROM vision_configuration_commands
             WHERE command_id = @command
             LIMIT 1
            """);
        command.Parameters.AddWithValue("@command", commandId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadVisionConfiguration(reader) : null;
    }

    private static VisionConfigurationOutboxItem ReadVisionConfiguration(
        SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.GetString(5),
        reader.GetString(6),
        ParseState(reader.GetString(7)),
        reader.GetInt64(8) == 1,
        reader.IsDBNull(9) ? null : reader.GetInt64(9),
        reader.IsDBNull(10) ? null : reader.GetString(10),
        ParseTimestamp(reader.GetString(11)),
        ParseTimestamp(reader.GetString(12)));

    private static VisionConfigurationOutboxState ParseState(string value) => value switch
    {
        "pending_apply" => VisionConfigurationOutboxState.PendingApply,
        "pending_ack" => VisionConfigurationOutboxState.PendingAck,
        "acked" => VisionConfigurationOutboxState.Acked,
        _ => throw new InvalidDataException("Vision configuration outbox state is invalid."),
    };

    private static DateTimeOffset ParseTimestamp(string value) => DateTimeOffset.Parse(
        value,
        CultureInfo.InvariantCulture,
        DateTimeStyles.RoundtripKind);

    private static void ValidateRegistration(VisionConfigurationOutboxRegistration value)
    {
        if (value.CommandId.Length != 36 ||
            !Guid.TryParseExact(value.CommandId, "D", out var parsed) ||
            parsed.ToString("D") != value.CommandId ||
            !VisionOptionsSnapshot.IsLowerHexSha256(value.ConfigDigest) ||
            value.OptionsDocument.Length is <= 0 or > 64 * 1024 ||
            value.EnvelopeNonce.Length is <= 0 or > 256 || value.EnvelopeNonce.Any(char.IsControl) ||
            !VisionOptionsSnapshot.IsLowerHexSha256(value.EnvelopeBinding) ||
            value.BundleUrl is { Length: > 2_048 } ||
            value.BundleSha256 is not null &&
            !VisionOptionsSnapshot.IsLowerHexSha256(value.BundleSha256))
            throw new ArgumentException("Vision configuration registration is invalid.");
    }
}
