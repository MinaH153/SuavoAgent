using SuavoAgent.Events;
using Xunit;

namespace SuavoAgent.Events.Tests;

public class EventBuilderTests
{
    private static EventBuilder MakeBuilder() => new(
        agentKeyId: "key-123",
        pharmacyId: "ph-salted-hash",
        missionCharterVersion: "v1.0.0",
        redactionRulesetVersion: "v1.0.0");

    [Fact]
    public void Build_UnknownType_Throws()
    {
        var b = MakeBuilder();
        var ex = Record.Exception(() => b.Build(
            "unknown.event.type",
            EventCategory.Runtime,
            EventSeverity.Info,
            new Dictionary<string, object?>()));
        Assert.IsType<UnknownEventTypeException>(ex);
    }

    [Fact]
    public void Build_WithPhi_ThrowsRedactionViolation()
    {
        var b = MakeBuilder();
        var ex = Record.Exception(() => b.Build(
            EventType.HeartbeatEmitted,
            EventCategory.Runtime,
            EventSeverity.Info,
            new Dictionary<string, object?>
            {
                ["operator_note"] = "Call John Smith at 555-123-4567"
            }));
        Assert.IsType<PhiRedactionViolationException>(ex);
    }

    [Fact]
    public void Build_CleanPayload_Succeeds()
    {
        var b = MakeBuilder();
        var evt = b.Build(
            EventType.HeartbeatEmitted,
            EventCategory.Runtime,
            EventSeverity.Info,
            new Dictionary<string, object?>
            {
                ["cpu_pct"] = 12.3,
                ["memory_mb"] = 128,
                ["services_running"] = new[] { "SuavoAgent.Core", "SuavoAgent.Broker" }
            });

        Assert.Equal(EventType.HeartbeatEmitted, evt.Type);
        Assert.Equal("ph-salted-hash", evt.PharmacyId);
        Assert.Equal("key-123", evt.ActorId);
        Assert.Equal(ActorType.Agent, evt.ActorType);
        Assert.Equal("v1.0.0", evt.MissionCharterVersion);
        Assert.Equal("v1.0.0", evt.RedactionRulesetVersion);
        Assert.NotEqual(Guid.Empty, evt.Id);
        Assert.NotEmpty(evt.Payload);
    }

    [Fact]
    public void AgentStarted_Helper_EmitsCorrectEvent()
    {
        var b = MakeBuilder();
        var evt = b.AgentStarted("3.13.7", new[] { "Core", "Broker", "Watchdog" }, 1234);

        Assert.Equal(EventType.AgentStarted, evt.Type);
        Assert.Equal(EventCategory.Runtime, evt.Category);
        Assert.Equal("3.13.7", evt.Payload["version"]);
    }

    [Fact]
    public void ServiceFailed_Helper_EmitsError()
    {
        var b = MakeBuilder();
        var evt = b.ServiceFailed("SuavoAgent.Core", 1, "OOM", 3);
        Assert.Equal(EventSeverity.Error, evt.Severity);
        Assert.Equal(EventCategory.Runtime, evt.Category);
    }

    [Fact]
    public void AttestationMismatch_Helper_EmitsCritical()
    {
        var b = MakeBuilder();
        var evt = b.AttestationMismatch("v3.13.7", new[] { "SuavoAgent.Core.exe" });
        Assert.Equal(EventSeverity.Critical, evt.Severity);
        Assert.Equal(EventCategory.Security, evt.Category);
    }

    [Fact]
    public void EventType_All_IsSuperset_OfDeclaredConstants()
    {
        // If someone adds a new constant but forgets to add it to the All
        // set, this test catches it.
        var declared = typeof(EventType)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.FieldType == typeof(string))
            .Select(f => (string)f.GetValue(null)!)
            .ToHashSet();

        foreach (var c in declared)
        {
            Assert.True(EventType.IsKnown(c), $"Constant '{c}' declared but missing from EventType.All");
        }
    }
}
