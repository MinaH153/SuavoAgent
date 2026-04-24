namespace SuavoAgent.Core.Mission;

/// <summary>
/// Thrown by <see cref="MissionCharterLoader.ValidateCharter"/> when a charter
/// fails a structural invariant. Carries the specific validation rule that
/// failed in the message so audit events can point at the offending field.
/// </summary>
public sealed class MissionCharterInvalidException : Exception
{
    public string ValidationRuleId { get; }

    public MissionCharterInvalidException(string validationRuleId, string message)
        : base(message)
    {
        ValidationRuleId = validationRuleId;
    }

    public MissionCharterInvalidException(string validationRuleId, string message, Exception inner)
        : base(message, inner)
    {
        ValidationRuleId = validationRuleId;
    }
}
