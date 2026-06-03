using System;

namespace SuavoAgent.Core.Health;

/// <summary>
/// Provides "is this pharmacy currently within business hours?" answer.
/// Implementation queries <c>pharmacy_profiles.hours</c> via the cloud
/// or a cached local copy. Failure modes (DB error, missing data) MUST
/// throw — the calculator catches and applies the conservative
/// off-hours fallback (extractionRecent = true).
/// </summary>
public interface IBusinessHoursProvider
{
    bool IsInsideBusinessHours(DateTimeOffset at);
}
