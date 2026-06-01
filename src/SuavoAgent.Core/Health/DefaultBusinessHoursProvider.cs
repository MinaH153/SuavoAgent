using System;

namespace SuavoAgent.Core.Health;

/// <summary>
/// Interim <see cref="IBusinessHoursProvider"/> default. Reports every time as
/// OUTSIDE business hours, so <see cref="HealthCompositeCalculator"/>'s
/// extraction_recent signal stays permissive (always true) and wiring up
/// composite emission cannot introduce extraction-staleness false-alarms on a
/// box that has no per-pharmacy business hours configured — or that does not
/// extract at all, like the test box.
///
/// The signals that actually surface the silent-blind failure
/// (helper_attached / ipc_connected, sourced from <see cref="HealthSignalsProvider"/>'s
/// IpcPipeServer, plus schema_canary_green) do NOT depend on business hours, so
/// the helper-down case is still detected. Replace with a per-pharmacy schedule
/// provider to enable extraction-staleness alarming. See roadmap H2 #13.
/// </summary>
public sealed class DefaultBusinessHoursProvider : IBusinessHoursProvider
{
    public bool IsInsideBusinessHours(DateTimeOffset at) => false;
}
