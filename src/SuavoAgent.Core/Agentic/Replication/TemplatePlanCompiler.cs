using System;
using System.Collections.Generic;
using System.Linq;
using SuavoAgent.Contracts.Behavioral;
using SuavoAgent.Contracts.Learning;

namespace SuavoAgent.Core.Agentic.Replication;

/// <summary>
/// One compiled step of a learned workflow, ready to drive through the same IActuator + ISafetyGate
/// chokepoint the NL loop uses. Actuating steps carry a <see cref="NextAction"/>; check steps
/// (WaitForElement / VerifyElement) carry none — the replayer perceives and asserts the expected
/// elements instead of actuating.
/// </summary>
public sealed record CompiledStep(
    int Ordinal,
    NextAction? Action,
    bool IsCheck,
    bool IsWrite,
    IReadOnlyList<ElementSignature> ExpectedVisible,
    IReadOnlyList<ElementSignature>? ExpectedAfter);

/// <summary>
/// "Replicate smartly": compiles a PHI-free learned <see cref="WorkflowTemplate"/> into an ordered,
/// executable plan. This is the watch-&amp;-learn replication core — given a template extracted from
/// observation, it produces the exact grounded actions to replay, which the replayer then runs
/// through the loop's fail-closed actuator/safety seam (so replay is gated, audited, never-blind
/// exactly like NL navigation).
///
/// Because templates store STRUCTURAL signatures (ControlType + AutomationId, never plaintext names),
/// click steps compile to a <c>click_by_signature</c> verb that resolves the element via
/// SelectorResolver.ToCondition — NOT the name-based <c>click_by_label</c> the NL reasoner emits.
/// Type steps carry only a whitelisted <see cref="KeyHintPlaceholder"/> (resolved at execution),
/// never operator text — PHI never enters the plan.
/// </summary>
public static class TemplatePlanCompiler
{
    public const string ClickBySignatureVerb = "click_by_signature";
    public const string TypeIntoFieldVerb = "type_into_field";
    public const string PressKeysVerb = "press_keys";

    public static IReadOnlyList<CompiledStep> Compile(WorkflowTemplate template)
    {
        if (template is null) throw new ArgumentNullException(nameof(template));
        return Compile(template.Steps);
    }

    public static IReadOnlyList<CompiledStep> Compile(IReadOnlyList<TemplateStep> steps)
    {
        if (steps is null) throw new ArgumentNullException(nameof(steps));
        return steps.OrderBy(s => s.Ordinal).Select(CompileStep).ToList();
    }

    public static CompiledStep CompileStep(TemplateStep step)
    {
        NextAction? action = step.Kind switch
        {
            TemplateStepKind.Click => NextAction.Act(ClickBySignatureVerb, SignatureParams(step.Target), "replay:click"),
            TemplateStepKind.Type => NextAction.Act(TypeIntoFieldVerb, TypeParams(step.Hint), "replay:type"),
            TemplateStepKind.PressKey => NextAction.Act(PressKeysVerb, PressParams(step.Hint), "replay:key"),
            _ => null, // WaitForElement / VerifyElement are perceive-and-assert checks, not actuation
        };

        var isCheck = step.Kind is TemplateStepKind.WaitForElement or TemplateStepKind.VerifyElement;
        return new CompiledStep(step.Ordinal, action, isCheck, step.IsWrite, step.ExpectedVisible, step.ExpectedAfter);
    }

    private static IReadOnlyDictionary<string, object?> SignatureParams(ElementSignature target) =>
        new Dictionary<string, object?>
        {
            ["automationId"] = target.AutomationId,
            ["controlType"] = target.ControlType,
            ["className"] = target.ClassName,
        };

    // Type carries only a whitelisted placeholder token (resolved to a non-PHI value at execution),
    // never plaintext — the extractor guarantees this and the compiler must not invent text.
    private static IReadOnlyDictionary<string, object?> TypeParams(KeyHint? hint) =>
        new Dictionary<string, object?> { ["placeholder"] = hint?.Placeholder?.ToString() };

    private static IReadOnlyDictionary<string, object?> PressParams(KeyHint? hint) =>
        new Dictionary<string, object?> { ["chords"] = hint?.KeyName };
}
