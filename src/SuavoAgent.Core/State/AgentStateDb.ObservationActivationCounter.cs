namespace SuavoAgent.Core.State;

public sealed partial class AgentStateDb
{
    /// <summary>
    /// Burns and returns one request counter inside a durable SQLite
    /// transaction. Gaps are safe; reuse after a crash is not.
    /// </summary>
    internal long NextObservationActivationRequestCounter(
        string agentId,
        string deviceKeyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceKeyId);
        lock (_connLock)
        {
            using var transaction = _conn.BeginTransaction();
            using (var initialize = CreateCommand(transaction, """
                INSERT OR IGNORE INTO observation_activation_request_counters(
                    agent_id, device_key_id, counter)
                VALUES (@agent, @key, 0)
                """))
            {
                initialize.Parameters.AddWithValue("@agent", agentId);
                initialize.Parameters.AddWithValue("@key", deviceKeyId);
                initialize.ExecuteNonQuery();
            }

            long counter;
            using (var increment = CreateCommand(transaction, """
                UPDATE observation_activation_request_counters
                   SET counter = counter + 1
                 WHERE agent_id = @agent AND device_key_id = @key
                RETURNING counter
                """))
            {
                increment.Parameters.AddWithValue("@agent", agentId);
                increment.Parameters.AddWithValue("@key", deviceKeyId);
                counter = Convert.ToInt64(increment.ExecuteScalar()
                    ?? throw new InvalidOperationException(
                        "Observation activation request counter is unavailable."));
            }
            transaction.Commit();
            return counter;
        }
    }
}
