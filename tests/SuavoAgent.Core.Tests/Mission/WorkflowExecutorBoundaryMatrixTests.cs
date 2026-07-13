using System.Collections;
using System.Reflection;
using System.Text.Json;
using SuavoAgent.Core.ActionGrammarV1;
using SuavoAgent.Core.ActionGrammarV1.Workflows;
using Xunit;

namespace SuavoAgent.Core.Tests.Mission;

public sealed class WorkflowExecutorBoundaryMatrixTests
{
    [Fact]
    public void ParameterProjection_PreservesOnlyTypedJsonShapesAndCoercesDeclaredBooleans()
    {
        var raw = JsonSerializer.Deserialize<JsonElement>("""
            {
              "string":"value",
              "truth":true,
              "falsity":false,
              "int32":17,
              "int64":2147483648,
              "floating":1.5,
              "nothing":null,
              "array":["text",17],
              "object":{"nested":true},
              "boolText":"true",
              "badBoolText":"not-bool"
            }
            """);
        var schema = new VerbParameterSchema(new[]
        {
            new VerbParameterSpec("boolText", typeof(bool), true),
            new VerbParameterSpec("badBoolText", typeof(bool), true),
        });

        var result = InvokeStatic<IReadOnlyDictionary<string, object?>>(
            "ParseStepParameters", raw, schema);

        Assert.Equal("value", result["string"]);
        Assert.Equal(true, result["truth"]);
        Assert.Equal(false, result["falsity"]);
        Assert.IsType<int>(result["int32"]);
        Assert.IsType<long>(result["int64"]);
        Assert.IsType<double>(result["floating"]);
        Assert.Null(result["nothing"]);
        Assert.Equal(new object?[] { "text", "17" }, Assert.IsType<List<object?>>(result["array"]));
        Assert.Equal("{\"nested\":true}", result["object"]);
        Assert.Equal(true, result["boolText"]);
        Assert.Equal(false, result["badBoolText"]);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"text\"")]
    [InlineData("1")]
    public void ParameterProjection_NonObjectIsEmpty(string json)
    {
        var raw = JsonSerializer.Deserialize<JsonElement>(json);
        var result = InvokeStatic<IReadOnlyDictionary<string, object?>>(
            "ParseStepParameters",
            raw,
            new VerbParameterSchema(Array.Empty<VerbParameterSpec>()));

        Assert.Empty(result);
    }

    [Theory]
    [InlineData("empty", null)]
    [InlineData("missing", null)]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("string-true", true)]
    [InlineData("string-false", false)]
    [InlineData("invalid-string", null)]
    [InlineData("number", null)]
    public void EffectiveDryRun_RecognizesOnlyBooleanOrParseableBooleanString(
        string shape,
        bool? expected)
    {
        IReadOnlyDictionary<string, object?> output = shape switch
        {
            "empty" => new Dictionary<string, object?>(),
            "missing" => new Dictionary<string, object?> { ["other"] = true },
            "true" => new Dictionary<string, object?> { ["dry_run"] = true },
            "false" => new Dictionary<string, object?> { ["dry_run"] = false },
            "string-true" => new Dictionary<string, object?> { ["dry_run"] = "true" },
            "string-false" => new Dictionary<string, object?> { ["dry_run"] = "FALSE" },
            "invalid-string" => new Dictionary<string, object?> { ["dry_run"] = "yes" },
            _ => new Dictionary<string, object?> { ["dry_run"] = 1 },
        };

        Assert.Equal(expected, InvokeNullableBool("ExtractEffectiveDryRun", output));
    }

    [Fact]
    public void EffectiveDryRun_NullOutputIsUnknown()
    {
        Assert.Null(InvokeNullableBool("ExtractEffectiveDryRun", null));
    }

    [Theory]
    [InlineData("precondition_failed: patient-specific detail", "precondition_failed")]
    [InlineData("execution_timeout:", "execution_timeout")]
    [InlineData("plain", "execution_failed")]
    [InlineData(":detail", "execution_failed")]
    public void ErrorReason_MapsToFixedPhiSafeAuditKind(
        string reason,
        string expectedKind)
    {
        var value = InvokeStatic<string>("MapAuditErrorKind", reason);

        Assert.Equal(expectedKind, value);
    }

    [Fact]
    public void StepIndex_IgnoresBlankIdsAndKeepsFirstDuplicate()
    {
        var steps = new[]
        {
            Step("first"),
            Step(""),
            Step(null),
            Step("duplicate"),
            Step("duplicate"),
        };

        var index = InvokeStatic<IReadOnlyDictionary<string, int>>(
            "BuildStepIdIndex",
            (object)steps);

        Assert.Equal(0, index["first"]);
        Assert.Equal(3, index["duplicate"]);
        Assert.Equal(2, index.Count);
    }

    [Theory]
    [InlineData(VerbDispatchOutcome.Success, "success")]
    [InlineData(VerbDispatchOutcome.Rejected, "rejected")]
    [InlineData(VerbDispatchOutcome.Failed, "failed")]
    [InlineData((VerbDispatchOutcome)100, "skipped")]
    [InlineData((VerbDispatchOutcome)999, "unknown")]
    public void OutcomeName_IsClosedAndUnknownSafe(VerbDispatchOutcome outcome, string expected)
    {
        Assert.Equal(expected, InvokeStatic<string>("OutcomeToString", outcome));
    }

    [Fact]
    public void ConditionGrammar_CoversHistoryOutputAndUnknownFailClosed()
    {
        var success = NewHistory(0, "first", VerbDispatchOutcome.Success,
            new Dictionary<string, object?> { ["mode"] = "READY", ["none"] = null });
        var skipped = NewHistory(1, "skipped", (VerbDispatchOutcome)100,
            new Dictionary<string, object?>());
        var history = NewHistoryDictionary((0, success), (1, skipped));
        var index = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["first"] = 0,
            ["missing-history"] = 7,
            ["skipped"] = 1,
        };

        Assert.True(Evaluate(null, null, history, index));
        Assert.True(Evaluate(new("always", null, null, null), null, history, index));
        Assert.False(Evaluate(new("never", null, null, null), success, history, index));
        Assert.False(Evaluate(new("previous_outcome", null, null, "success"), null, history, index));
        Assert.True(Evaluate(new("previous_outcome", null, null, "SUCCESS"), success, history, index));
        Assert.False(Evaluate(new("previous_outcome", null, null, "failed"), success, history, index));
        Assert.True(Evaluate(new("step_outcome", "first", null, "success"), null, history, index));
        Assert.True(Evaluate(new("step_outcome", "skipped", null, "skipped"), null, history, index));
        Assert.False(Evaluate(new("step_outcome", null, null, "success"), null, history, index));
        Assert.False(Evaluate(new("step_outcome", "unknown", null, "success"), null, history, index));
        Assert.False(Evaluate(new("step_outcome", "missing-history", null, "success"), null, history, index));
        Assert.True(Evaluate(new("step_output", "first", "mode", "ready"), null, history, index));
        Assert.True(Evaluate(new("step_output", "first", "none", null), null, history, index));
        Assert.False(Evaluate(new("step_output", "first", "unknown", "ready"), null, history, index));
        Assert.False(Evaluate(new("step_output", "unknown", "mode", "ready"), null, history, index));
        Assert.False(Evaluate(new("future_expression", null, null, null), null, history, index));
    }

    [Fact]
    public void ControlFlowGrammar_CoversEveryDirectiveAndFailsUnknownClosed()
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["first"] = 0,
            ["last"] = 2,
            ["negative"] = -1,
            ["past-end"] = 3,
        };

        AssertFlow(null, 0, VerbDispatchOutcome.Success, null, index, 3, 1, null, null);
        AssertFlow(null, 0, VerbDispatchOutcome.Failed, "boom", index, 3, 0, WorkflowRunOutcome.Failed, "boom");
        AssertFlow(new("continue", null, null, null, null), 0, VerbDispatchOutcome.Success, null, index, 3, 1, null, null);
        AssertFlow(new("continue", null, null, null, null), 0, VerbDispatchOutcome.Rejected, "denied", index, 3, 0, WorkflowRunOutcome.Failed, "denied");
        AssertFlow(new("goto", "last", null, null, null), 0, VerbDispatchOutcome.Success, null, index, 3, 2, null, null);
        AssertFlow(new("goto", null, null, null, null), 0, VerbDispatchOutcome.Success, null, index, 3, 0, WorkflowRunOutcome.Failed, "goto_target_unresolved:");
        AssertFlow(new("goto", "unknown", null, null, null), 0, VerbDispatchOutcome.Success, null, index, 3, 0, WorkflowRunOutcome.Failed, "goto_target_unresolved:unknown");
        AssertFlow(new("goto", "negative", null, null, null), 0, VerbDispatchOutcome.Success, null, index, 3, 0, WorkflowRunOutcome.Failed, "goto_target_unresolved:negative");
        AssertFlow(new("goto", "past-end", null, null, null), 0, VerbDispatchOutcome.Success, null, index, 3, 0, WorkflowRunOutcome.Failed, "goto_target_unresolved:past-end");
        AssertFlow(new("end", null, null, "completed", "done"), 0, VerbDispatchOutcome.Failed, "boom", index, 3, 0, WorkflowRunOutcome.Completed, "done");
        AssertFlow(new("end", null, null, "failed", null), 0, VerbDispatchOutcome.Success, "boom", index, 3, 0, WorkflowRunOutcome.Failed, "boom");
        AssertFlow(new("end", null, null, "aborted", "stop"), 0, VerbDispatchOutcome.Success, null, index, 3, 0, WorkflowRunOutcome.Aborted, "stop");
        AssertFlow(new("end", null, null, "unknown", null), 0, VerbDispatchOutcome.Success, null, index, 3, 0, WorkflowRunOutcome.Completed, null);
        AssertFlow(new("end", null, null, "unknown", null), 0, VerbDispatchOutcome.Failed, "boom", index, 3, 0, WorkflowRunOutcome.Failed, "boom");
        AssertFlow(new("retry", null, 2, null, null), 0, VerbDispatchOutcome.Failed, "boom", index, 3, 0, WorkflowRunOutcome.Failed, "retry_exhausted:boom");
        AssertFlow(new("future", null, null, null, null), 0, VerbDispatchOutcome.Success, null, index, 3, 0, WorkflowRunOutcome.Failed, "unknown_control_flow:future");
    }

    private static WorkflowStepDto Step(string? id) => new(
        "press_keys",
        "1.0.0",
        null,
        JsonSerializer.Deserialize<JsonElement>("{}"),
        null,
        id);

    private static bool Evaluate(
        WorkflowConditionDto? condition,
        object? previous,
        object history,
        IReadOnlyDictionary<string, int> index)
    {
        var method = GetStatic("EvaluateCondition");
        return Assert.IsType<bool>(method.Invoke(null, [condition, previous, history, index]));
    }

    private static object NewHistory(
        int index,
        string? id,
        VerbDispatchOutcome outcome,
        IReadOnlyDictionary<string, object?> output)
    {
        var type = HistoryType();
        var constructor = type.GetConstructors(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(candidate => candidate.GetParameters().Length == 4);
        return constructor.Invoke([index, id, outcome, output]);
    }

    private static object NewHistoryDictionary(params (int Index, object Entry)[] entries)
    {
        var type = typeof(Dictionary<,>).MakeGenericType(typeof(int), HistoryType());
        var dictionary = Assert.IsAssignableFrom<IDictionary>(Activator.CreateInstance(type));
        foreach (var entry in entries)
            dictionary.Add(entry.Index, entry.Entry);
        return dictionary;
    }

    private static Type HistoryType() =>
        typeof(WorkflowExecutor).GetNestedType(
            "StepHistoryEntry",
            BindingFlags.NonPublic) ?? throw new InvalidOperationException("History type missing");

    private static void AssertFlow(
        WorkflowControlFlowDto? directive,
        int current,
        VerbDispatchOutcome outcome,
        string? failure,
        IReadOnlyDictionary<string, int> index,
        int total,
        int expectedNext,
        WorkflowRunOutcome? expectedOutcome,
        string? expectedReason)
    {
        var raw = GetStatic("ApplyControlFlow").Invoke(
            null,
            [directive, current, outcome, failure, index, total]);
        Assert.NotNull(raw);
        var tupleType = raw!.GetType();
        Assert.Equal(expectedNext, tupleType.GetField("Item1")!.GetValue(raw));
        var terminal = tupleType.GetField("Item2")!.GetValue(raw);
        if (expectedOutcome is null)
        {
            Assert.Null(terminal);
            return;
        }

        Assert.NotNull(terminal);
        var terminalType = terminal!.GetType();
        Assert.Equal(expectedOutcome, terminalType.GetProperty("Outcome")!.GetValue(terminal));
        Assert.Equal(expectedReason, terminalType.GetProperty("Reason")!.GetValue(terminal));
    }

    private static bool? InvokeNullableBool(string methodName, object? argument) =>
        (bool?)GetStatic(methodName).Invoke(null, [argument]);

    private static T InvokeStatic<T>(string methodName, params object?[] arguments) =>
        Assert.IsAssignableFrom<T>(GetStatic(methodName).Invoke(null, arguments));

    private static MethodInfo GetStatic(string methodName) =>
        typeof(WorkflowExecutor).GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException($"Missing {methodName}");
}
