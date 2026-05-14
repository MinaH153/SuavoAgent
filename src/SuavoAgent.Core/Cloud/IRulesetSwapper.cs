using SuavoAgent.Diagnostics;

namespace SuavoAgent.Core.Cloud;

/// <summary>
/// Thin abstraction over <see cref="Wire.SwapRuleset"/> so workers can
/// publish a new ruleset generation through a DI-injectable seam that
/// tests can intercept.
/// </summary>
/// <remarks>
/// Production wiring registers <see cref="WireRulesetSwapper"/>; tests
/// register a recording fake to assert the worker called swap with the
/// correct ruleset under each failure-mode scenario without polluting
/// Wire's process-wide static state.
/// </remarks>
public interface IRulesetSwapper
{
    /// <summary>Currently-active ruleset version int (reads through to <see cref="Wire.RulesetVersionInt"/>).</summary>
    int CurrentVersionInt { get; }

    /// <summary>Atomically replace the active ruleset + derived components.</summary>
    void Swap(RulesetV1 ruleset);
}

/// <summary>Production swapper — delegates to <see cref="Wire"/>.</summary>
public sealed class WireRulesetSwapper : IRulesetSwapper
{
    public int CurrentVersionInt => Wire.RulesetVersionInt;
    public void Swap(RulesetV1 ruleset) => Wire.SwapRuleset(ruleset);
}
