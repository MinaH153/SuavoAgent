namespace SuavoAgent.Core.State;

public sealed partial class AgentStateDb
{
    private void ApplyObservationActivationRequestCounterMigration()
    {
        ApplyMigrationIfNeeded(42,
            "Monotonic device-signed observation activation lease requests",
            """
            CREATE TABLE observation_activation_request_counters (
                agent_id TEXT NOT NULL,
                device_key_id TEXT NOT NULL CHECK(
                    length(device_key_id) = 64
                    AND device_key_id NOT GLOB '*[^0-9a-f]*'),
                counter INTEGER NOT NULL CHECK(counter >= 0),
                PRIMARY KEY(agent_id, device_key_id)
            );
            """);
    }
}
