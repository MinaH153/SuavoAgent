using Microsoft.Data.Sqlite;

namespace SuavoAgent.Core.State;

public sealed record UpdateCommandReceiptRegistration(
    bool Accepted,
    bool IsReplay,
    string State,
    string Code);

public sealed partial class AgentStateDb
{
    public UpdateCommandReceiptRegistration RegisterUpdateCommandReceipt(
        string commandId,
        string envelopeNonce,
        string dataHash,
        string targetVersion)
    {
        if (!Guid.TryParseExact(commandId, "D", out _) ||
            string.IsNullOrWhiteSpace(envelopeNonce) ||
            string.IsNullOrWhiteSpace(dataHash) ||
            string.IsNullOrWhiteSpace(targetVersion))
            return new(false, false, "rejected", "update_receipt_identity_invalid");

        lock (_connLock)
        {
            using var transaction = _conn.BeginTransaction();
            using var select = _conn.CreateCommand();
            select.Transaction = transaction;
            select.CommandText = """
                SELECT envelope_nonce, data_hash, target_version, state
                FROM update_command_receipts
                WHERE command_id = @commandId OR envelope_nonce = @nonce
                LIMIT 1
                """;
            select.Parameters.AddWithValue("@commandId", commandId);
            select.Parameters.AddWithValue("@nonce", envelopeNonce);
            using var reader = select.ExecuteReader();
            if (reader.Read())
            {
                var exact =
                    string.Equals(reader.GetString(0), envelopeNonce, StringComparison.Ordinal) &&
                    string.Equals(reader.GetString(1), dataHash, StringComparison.Ordinal) &&
                    string.Equals(reader.GetString(2), targetVersion, StringComparison.Ordinal);
                var state = reader.GetString(3);
                reader.Close();
                transaction.Commit();
                return exact
                    ? new(true, true, state, "update_receipt_exact_replay")
                    : new(false, false, state, "update_receipt_binding_conflict");
            }
            reader.Close();

            var now = DateTimeOffset.UtcNow.ToString("O");
            using var insert = _conn.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO update_command_receipts (
                    command_id, envelope_nonce, data_hash, target_version,
                    state, registered_at, updated_at
                ) VALUES (
                    @commandId, @nonce, @dataHash, @targetVersion,
                    'pending_stage', @now, @now
                )
                """;
            insert.Parameters.AddWithValue("@commandId", commandId);
            insert.Parameters.AddWithValue("@nonce", envelopeNonce);
            insert.Parameters.AddWithValue("@dataHash", dataHash);
            insert.Parameters.AddWithValue("@targetVersion", targetVersion);
            insert.Parameters.AddWithValue("@now", now);
            insert.ExecuteNonQuery();
            transaction.Commit();
            return new(true, false, "pending_stage", "update_receipt_registered");
        }
    }

    public void MarkUpdateCommandReceipt(string commandId, string state)
    {
        if (!Guid.TryParseExact(commandId, "D", out _) ||
            state is not ("staged" or "confirmed"))
            throw new ArgumentException("Invalid update receipt transition.");

        lock (_connLock)
        {
            using var command = _conn.CreateCommand();
            command.CommandText = """
                UPDATE update_command_receipts
                SET state = @state, updated_at = @now
                WHERE command_id = @commandId
                  AND state IN ('pending_stage','staged')
                """;
            command.Parameters.AddWithValue("@state", state);
            command.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("@commandId", commandId);
            if (command.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("Update receipt transition was not applied.");
        }
    }
}
