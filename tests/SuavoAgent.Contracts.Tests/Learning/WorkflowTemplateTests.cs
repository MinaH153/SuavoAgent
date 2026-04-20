using System.Collections.Generic;
using SuavoAgent.Contracts.Behavioral;
using SuavoAgent.Contracts.Learning;
using Xunit;

namespace SuavoAgent.Contracts.Tests.Learning;

public class WorkflowTemplateTests
{
    private static ElementSignature Sig(string ctrl, string id, string? cls = null) =>
        new(ctrl, id, cls);

    private static TemplateStep MakeStep(int ord, TemplateStepKind kind, ElementSignature target,
        IReadOnlyList<ElementSignature> visible, bool isWrite = false,
        IReadOnlyList<ElementSignature>? after = null, string? shape = null, KeyHint? hint = null) =>
        new(ord, kind, target, visible,
            MinElementsRequired: System.Math.Max(1, (int)System.Math.Ceiling(visible.Count * 0.8)),
            ExpectedAfter: after,
            IsWrite: isWrite,
            CorrelatedQueryShapeHash: shape,
            StepConfidence: 0.9,
            Hint: hint);

    // ──────────────────────── TemplateStep invariants ────────────────────────

    [Fact]
    public void TemplateStep_Writeback_RequiresExpectedAfter()
    {
        var target = Sig("Button", "btnApprove");
        var visible = new[] { target, Sig("Edit", "txtRxNumber") };
        var ex = Assert.Throws<System.ArgumentException>(() =>
            new TemplateStep(0, TemplateStepKind.Click, target, visible, 2,
                ExpectedAfter: null, IsWrite: true,
                CorrelatedQueryShapeHash: "abc123", StepConfidence: 0.9, Hint: null));
        Assert.Contains("ExpectedAfter", ex.Message);
    }

    [Fact]
    public void TemplateStep_Writeback_AcceptsNonEmptyExpectedAfter()
    {
        var target = Sig("Button", "btnApprove");
        var visible = new[] { target, Sig("Edit", "txtRxNumber") };
        var after = new[] { Sig("Window", "wndApproved") };
        var step = new TemplateStep(0, TemplateStepKind.Click, target, visible, 2,
            ExpectedAfter: after, IsWrite: true,
            CorrelatedQueryShapeHash: "abc123", StepConfidence: 0.9, Hint: null);
        Assert.True(step.IsWrite);
        Assert.NotNull(step.ExpectedAfter);
    }

    [Fact]
    public void TemplateStep_MinRequired_CannotExceedExpectedVisibleCount()
    {
        var target = Sig("Button", "btnApprove");
        var visible = new[] { target };
        Assert.Throws<System.ArgumentOutOfRangeException>(() =>
            new TemplateStep(0, TemplateStepKind.Click, target, visible,
                MinElementsRequired: 5,
                ExpectedAfter: null, IsWrite: false,
                CorrelatedQueryShapeHash: null, StepConfidence: 0.9, Hint: null));
    }

    [Fact]
    public void TemplateStep_CanonicalLine_Deterministic()
    {
        var target = Sig("Button", "btnApprove", "WinForms.Button");
        var visible = new[] { target };
        var step = new TemplateStep(0, TemplateStepKind.Click, target, visible, 1,
            null, false, "abc", 0.91234, new KeyHint("Enter", null));
        var line = step.CanonicalLine();
        Assert.Equal(
            "0\tClick\tButton|btnApprove|WinForms.Button\t0\tabc\t0.912\tEnter|",
            line);
    }

    // ──────────────────────── WorkflowTemplate.ComputeStepsHash ────────────────────────

    [Fact]
    public void StepsHash_IsDeterministic()
    {
        var target = Sig("Button", "btnApprove");
        var steps = new[]
        {
            MakeStep(0, TemplateStepKind.Click, target, new[] { target }),
            MakeStep(1, TemplateStepKind.WaitForElement, Sig("Window", "wndReady"),
                new[] { Sig("Window", "wndReady") }),
        };
        var h1 = WorkflowTemplate.ComputeStepsHash(steps);
        var h2 = WorkflowTemplate.ComputeStepsHash(steps);
        Assert.Equal(h1, h2);
        Assert.Matches("^[0-9a-f]{64}$", h1);
    }

    [Fact]
    public void StepsHash_OrderIndependentByOrdinal()
    {
        // Even if caller lists steps out of ordinal order, the canonical hash
        // should match a properly-ordered list. Guarantees idempotency.
        var a = MakeStep(0, TemplateStepKind.Click, Sig("Button", "a"), new[] { Sig("Button", "a") });
        var b = MakeStep(1, TemplateStepKind.Click, Sig("Button", "b"), new[] { Sig("Button", "b") });

        var inOrder = WorkflowTemplate.ComputeStepsHash(new[] { a, b });
        var outOfOrder = WorkflowTemplate.ComputeStepsHash(new[] { b, a });
        Assert.Equal(inOrder, outOfOrder);
    }

    [Fact]
    public void StepsHash_ChangesWhenStepsDiffer()
    {
        var a = MakeStep(0, TemplateStepKind.Click, Sig("Button", "btnA"),
            new[] { Sig("Button", "btnA") });
        var a2 = MakeStep(0, TemplateStepKind.Click, Sig("Button", "btnZ"),
            new[] { Sig("Button", "btnZ") });
        var h1 = WorkflowTemplate.ComputeStepsHash(new[] { a });
        var h2 = WorkflowTemplate.ComputeStepsHash(new[] { a2 });
        Assert.NotEqual(h1, h2);
    }

    [Fact]
    public void StepsHash_Empty_Throws()
    {
        Assert.Throws<System.ArgumentException>(() =>
            WorkflowTemplate.ComputeStepsHash(System.Array.Empty<TemplateStep>()));
    }

    // ──────────────────────── ComputeScreenSignature ────────────────────────

    [Fact]
    public void ScreenSignature_OrderIndependent()
    {
        var a = Sig("Button", "btnOk", "WinForms.Button");
        var b = Sig("Edit", "txtSearch");
        Assert.Equal(
            WorkflowTemplate.ComputeScreenSignature(new[] { a, b }),
            WorkflowTemplate.ComputeScreenSignature(new[] { b, a }));
    }

    [Fact]
    public void ScreenSignature_ChangesWhenElementChanges()
    {
        var a = Sig("Button", "btnOk");
        var b = Sig("Edit", "txtSearch");
        var c = Sig("Edit", "txtOther");
        Assert.NotEqual(
            WorkflowTemplate.ComputeScreenSignature(new[] { a, b }),
            WorkflowTemplate.ComputeScreenSignature(new[] { a, c }));
    }

    [Fact]
    public void TemplateId_StableAcrossRecomputation()
    {
        var target = Sig("Button", "btnApprove");
        var steps = new[] { MakeStep(0, TemplateStepKind.Click, target, new[] { target }) };
        var stepsHash = WorkflowTemplate.ComputeStepsHash(steps);
        var screenSig = WorkflowTemplate.ComputeScreenSignature(new[] { target });
        var id1 = WorkflowTemplate.ComputeTemplateId(screenSig, stepsHash);
        var id2 = WorkflowTemplate.ComputeTemplateId(screenSig, stepsHash);
        Assert.Equal(id1, id2);
    }
}
