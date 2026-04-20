using System.Collections.Generic;
using SuavoAgent.Contracts.Behavioral;

namespace SuavoAgent.Contracts.Learning;

public enum TemplateStepKind
{
    Click,
    Type,
    PressKey,
    WaitForElement,
    VerifyElement,
}

/// <summary>
/// One step inside a WorkflowTemplate. Fields are all PHI-free by construction
/// (ElementSignature is GREEN-tier only; KeyHint is a closed type).
///
/// Invariants enforced by construction:
///   - Ordinal >= 0.
///   - ExpectedVisible has at least MinElementsRequired items.
///   - MinElementsRequired >= 1 (degenerate zero-required predicates rejected).
///   - ExpectedAfter required when IsWrite is true (writeback safety — Codex
///     Area 2 BLOCK fix). TemplateRuleGenerator additionally requires
///     VerifyBefore to be derivable from ExpectedVisible for IsWrite steps;
///     that check lives in the generator, not here.
/// </summary>
public sealed record TemplateStep
{
    public int Ordinal { get; }
    public TemplateStepKind Kind { get; }
    public ElementSignature Target { get; }
    public IReadOnlyList<ElementSignature> ExpectedVisible { get; }
    public int MinElementsRequired { get; }
    public IReadOnlyList<ElementSignature>? ExpectedAfter { get; }
    public bool IsWrite { get; }
    public string? CorrelatedQueryShapeHash { get; }
    public double StepConfidence { get; }
    public KeyHint? Hint { get; }

    public TemplateStep(
        int Ordinal,
        TemplateStepKind Kind,
        ElementSignature Target,
        IReadOnlyList<ElementSignature> ExpectedVisible,
        int MinElementsRequired,
        IReadOnlyList<ElementSignature>? ExpectedAfter,
        bool IsWrite,
        string? CorrelatedQueryShapeHash,
        double StepConfidence,
        KeyHint? Hint)
    {
        if (Ordinal < 0)
            throw new System.ArgumentOutOfRangeException(nameof(Ordinal), "Ordinal must be >= 0");
        if (Target is null)
            throw new System.ArgumentNullException(nameof(Target));
        if (ExpectedVisible is null || ExpectedVisible.Count == 0)
            throw new System.ArgumentException("ExpectedVisible must not be empty", nameof(ExpectedVisible));
        if (MinElementsRequired < 1)
            throw new System.ArgumentOutOfRangeException(nameof(MinElementsRequired), "MinElementsRequired must be >= 1");
        if (MinElementsRequired > ExpectedVisible.Count)
            throw new System.ArgumentOutOfRangeException(nameof(MinElementsRequired),
                "MinElementsRequired cannot exceed ExpectedVisible.Count");
        if (IsWrite && (ExpectedAfter is null || ExpectedAfter.Count == 0))
            throw new System.ArgumentException(
                "Writeback steps (IsWrite=true) MUST carry a non-empty ExpectedAfter — v3.12 safety gate.",
                nameof(ExpectedAfter));
        if (StepConfidence < 0.0 || StepConfidence > 1.0)
            throw new System.ArgumentOutOfRangeException(nameof(StepConfidence), "StepConfidence must be in [0,1]");

        this.Ordinal = Ordinal;
        this.Kind = Kind;
        this.Target = Target;
        this.ExpectedVisible = ExpectedVisible;
        this.MinElementsRequired = MinElementsRequired;
        this.ExpectedAfter = ExpectedAfter;
        this.IsWrite = IsWrite;
        this.CorrelatedQueryShapeHash = CorrelatedQueryShapeHash;
        this.StepConfidence = StepConfidence;
        this.Hint = Hint;
    }

    /// <summary>
    /// Canonical byte layout for a single step as a line inside
    /// <c>WorkflowTemplate.StepsHash</c>. See spec §2.4.
    /// Tabs separate fields; empty fields render as "".
    /// </summary>
    public string CanonicalLine()
    {
        var corr = CorrelatedQueryShapeHash ?? string.Empty;
        var hint = (Hint ?? new KeyHint(null, null)).CanonicalRepr;
        return string.Join('\t',
            Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Kind.ToString(),
            Target.CanonicalRepr,
            IsWrite ? "1" : "0",
            corr,
            StepConfidence.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
            hint);
    }
}
