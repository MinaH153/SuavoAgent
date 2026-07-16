using System.Reflection;
using System.Text.Json;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.Workers;

/// <summary>
/// Exhaustive boundary matrices for the PHI-free heartbeat command surfaces.
/// These tests intentionally exercise the real closed-schema predicates so a
/// future field addition cannot accidentally create a free-form egress channel.
/// </summary>
public partial class HeartbeatWorkerTests
{
    [Theory]
    [MemberData(nameof(HealthProbePayloads))]
    public void HealthProbePayload_EnforcesClosedNonPhiSchema(string json, bool rejected)
    {
        var payload = JsonSerializer.Deserialize<JsonElement>(json);

        Assert.Equal(rejected, InvokeStaticBool("ContainsUnsafeHealthProbeField", payload));
    }

    [Theory]
    [MemberData(nameof(ComputerUsePayloads))]
    public void ComputerUsePayload_EnforcesSyntheticNonPhiSchema(
        string command,
        string json,
        bool rejected)
    {
        var payload = JsonSerializer.Deserialize<JsonElement>(json);

        Assert.Equal(
            rejected,
            InvokeStaticBool("ContainsUnsafeComputerUseField", payload, command));
    }

    [Theory]
    [MemberData(nameof(IntentCursorPayloads))]
    public void IntentCursorPayload_RejectsTextAndAllowsOnlyBoundedVisualFields(
        string json,
        bool rejected)
    {
        var payload = JsonSerializer.Deserialize<JsonElement>(json);

        Assert.Equal(rejected, InvokeStaticBool("ContainsUnsafeIntentCursorField", payload));
    }

    [Theory]
    [InlineData("command_123", true)]
    [InlineData("a-b_c9", true)]
    [InlineData("UPPER", false)]
    [InlineData("contains.dot", false)]
    [InlineData("contains space", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void StructuralCommandIdentifier_IsLowerAsciiAndBounded(string? value, bool accepted)
    {
        Assert.Equal(accepted, InvokeStaticBool("IsStructuralIdentifier", value));
    }

    [Fact]
    public void StructuralCommandIdentifier_RejectsOver128Characters()
    {
        Assert.False(InvokeStaticBool("IsStructuralIdentifier", new string('a', 129)));
    }

    [Theory]
    [InlineData("paused", "paused")]
    [InlineData("stopped_by_operator", "stopped_by_operator")]
    [InlineData("with-dash", null)]
    [InlineData("UPPER", null)]
    [InlineData("with space", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void StructuralReasonCode_IsLowerAsciiUnderscoreOnly(string? value, string? expected)
    {
        Assert.Equal(expected, InvokeStaticString("StructuralReasonCode", value));
    }

    [Fact]
    public void StructuralReasonCode_RejectsOver64Characters()
    {
        Assert.Null(InvokeStaticString("StructuralReasonCode", new string('a', 65)));
    }

    public static IEnumerable<object[]> HealthProbePayloads()
    {
        yield return ["{}", false];
        foreach (var reason in new[]
                 {
                     "dashboard_diagnostics", "post_install_probe", "operator_requested",
                     "before_repair", "after_repair", "watchdog_unhealthy",
                 })
        {
            yield return [JsonSerializer.Serialize(new { reason }), false];
        }

        yield return [JsonSerializer.Serialize(new { commandId = new string('a', 128) }), false];
        yield return [JsonSerializer.Serialize(new { requester_id = "operator_1" }), false];
        yield return ["[]", true];
        yield return ["null", true];
        yield return [JsonSerializer.Serialize(new { reason = "free form patient detail" }), true];
        yield return [JsonSerializer.Serialize(new { reason = 1 }), true];
        yield return [JsonSerializer.Serialize(new { commandId = 1 }), true];
        yield return [JsonSerializer.Serialize(new { commandId = new string('a', 129) }), true];
        yield return [JsonSerializer.Serialize(new { requesterId = new { nested = true } }), true];
        yield return [JsonSerializer.Serialize(new { unknown = "value" }), true];

        foreach (var field in BlockedPhiFields())
            yield return [JsonSerializer.Serialize(new Dictionary<string, object?> { [field] = "synthetic" }), true];
    }

    public static IEnumerable<object[]> ComputerUsePayloads()
    {
        foreach (var pack in new[] { "workstation_health", "pioneerrx_shadow", "inbox_shadow" })
        {
            yield return [
                "computer_use_observe",
                JsonSerializer.Serialize(new { pack, mode = "synthetic", commandId = "cmd_1" }),
                false];
        }

        foreach (var proposal in new[]
                 {
                     "run_diagnostics", "queue_repair", "show_intent_cursor", "open_delivery_inbox",
                 })
        {
            yield return [
                "computer_use_propose",
                JsonSerializer.Serialize(new
                {
                    pack = "workstation_health",
                    mode = "synthetic",
                    proposal,
                    requesterId = new string('a', 128),
                }),
                false];
        }

        yield return ["computer_use_observe", "[]", true];
        yield return ["computer_use_observe", "null", true];
        yield return ["computer_use_observe", JsonSerializer.Serialize(new { proposal = "run_diagnostics" }), true];
        yield return ["computer_use_propose", JsonSerializer.Serialize(new { proposal = "free_form" }), true];
        yield return ["computer_use_propose", JsonSerializer.Serialize(new { proposal = 1 }), true];
        yield return ["computer_use_observe", JsonSerializer.Serialize(new { pack = "unknown" }), true];
        yield return ["computer_use_observe", JsonSerializer.Serialize(new { pack = 1 }), true];
        yield return ["computer_use_observe", JsonSerializer.Serialize(new { mode = "live" }), true];
        yield return ["computer_use_observe", JsonSerializer.Serialize(new { mode = 1 }), true];
        yield return ["computer_use_observe", JsonSerializer.Serialize(new { commandId = new string('a', 129) }), true];
        yield return ["computer_use_observe", JsonSerializer.Serialize(new { requesterId = 1 }), true];
        yield return ["computer_use_observe", JsonSerializer.Serialize(new { unknown = "value" }), true];
        yield return ["computer_use_observe", JsonSerializer.Serialize(new { pack = new[] { "workstation_health" } }), true];

        foreach (var field in BlockedComputerUseFields())
        {
            yield return [
                "computer_use_observe",
                JsonSerializer.Serialize(new Dictionary<string, object?> { [field] = "synthetic" }),
                true];
        }
    }

    public static IEnumerable<object[]> IntentCursorPayloads()
    {
        yield return ["{}", false];
        yield return [JsonSerializer.Serialize(new { coordinateSpace = "screen" }), false];
        foreach (var tone in new[] { "agent", "attention", "success", "warning" })
            yield return [JsonSerializer.Serialize(new { tone }), false];
        yield return [JsonSerializer.Serialize(new { anchor = "primary_center", toAnchor = "primary_center" }), false];
        foreach (var easing in new[] { "linear", "ease_in_out_cubic" })
            yield return [JsonSerializer.Serialize(new { easing }), false];
        yield return [JsonSerializer.Serialize(new
        {
            x = 1.0, y = 2.0, toX = 3.0, toY = 4.0, durationMs = 5,
            diameterPx = 6, opacity = 0.5, commandId = "cmd_1", requesterId = "operator_1",
        }), false];

        yield return ["[]", true];
        yield return ["null", true];
        yield return [JsonSerializer.Serialize(new { coordinateSpace = "window" }), true];
        yield return [JsonSerializer.Serialize(new { coordinateSpace = 1 }), true];
        yield return [JsonSerializer.Serialize(new { tone = "free_form" }), true];
        yield return [JsonSerializer.Serialize(new { tone = 1 }), true];
        yield return [JsonSerializer.Serialize(new { anchor = "top_left" }), true];
        yield return [JsonSerializer.Serialize(new { toAnchor = 1 }), true];
        yield return [JsonSerializer.Serialize(new { easing = "free_form" }), true];
        yield return [JsonSerializer.Serialize(new { easing = 1 }), true];
        yield return [JsonSerializer.Serialize(new { x = "1" }), true];
        yield return [JsonSerializer.Serialize(new { opacity = true }), true];
        yield return [JsonSerializer.Serialize(new { commandId = new string('a', 129) }), true];
        yield return [JsonSerializer.Serialize(new { requesterId = 1 }), true];
        yield return [JsonSerializer.Serialize(new { unknown = 1 }), true];
        yield return [JsonSerializer.Serialize(new { x = new[] { 1 } }), true];

        foreach (var field in BlockedPhiFields())
            yield return [JsonSerializer.Serialize(new Dictionary<string, object?> { [field] = "sensitive" }), true];
    }

    private static IEnumerable<string> BlockedComputerUseFields() =>
        BlockedPhiFields().Concat(new[]
        {
            "screenshot", "image", "ocr", "click", "type", "key", "mouse",
            "coordinates", "address", "phone",
        });

    private static IEnumerable<string> BlockedPhiFields() => new[]
    {
        "text", "label", "windowTitle", "rx", "rxNumber", "rxId",
        "prescription", "prescriptionId", "patient", "patientId", "patientName",
        "patientFirstName", "patientLastName", "medication", "ndc",
    };

    private static bool InvokeStaticBool(string methodName, params object?[] arguments)
    {
        var method = typeof(HeartbeatWorker).GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return Assert.IsType<bool>(method!.Invoke(null, arguments));
    }

    private static string? InvokeStaticString(string methodName, params object?[] arguments)
    {
        var method = typeof(HeartbeatWorker).GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (string?)method!.Invoke(null, arguments);
    }
}
