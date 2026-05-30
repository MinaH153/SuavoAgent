using System;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using Xunit;

namespace SuavoAgent.Core.Tests.Cloud;

/// <summary>
/// The outbound PHI guard value-scans EVERY non-PHI endpoint, the heartbeat
/// included — patient data must never ride control-plane telemetry. The guard
/// historically gave no diagnostic when it blocked, which made a false-positive
/// on legitimate telemetry undebuggable (it silently took the agent offline).
/// These tests pin the behaviour: the guard still blocks PHI on any path, AND
/// the exception names the offending FIELD (never the value) so a block can be
/// pinpointed from logs.
/// </summary>
public sealed class OutboundPhiGuardTests
{
    private static readonly AgentOptions Options = new();

    [Fact]
    public void Blocks_a_phi_shaped_value_and_names_the_field_on_the_heartbeat()
    {
        var body = @"{ ""detail"": ""Patient Jane Rivera DOB 04/12/1980 phone 555-123-4567"" }";

        var ex = Assert.Throws<InvalidOperationException>(
            () => OutboundPhiGuard.AssertAllowed("/api/agent/heartbeat", body, Options));

        // PHI-safe diagnostic: the field name surfaces, the value never does.
        Assert.Contains("field: detail", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Rivera", ex.Message);
    }

    [Fact]
    public void Blocks_a_blocked_field_name_and_names_it()
    {
        var body = @"{ ""patientName"": ""x"" }";

        var ex = Assert.Throws<InvalidOperationException>(
            () => OutboundPhiGuard.AssertAllowed("/api/agent/heartbeat", body, Options));

        Assert.Contains("field: patientname", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Names_a_nested_offending_field()
    {
        var body = @"{ ""runtimeHealth"": { ""note"": ""call 555-123-4567 back"" } }";

        var ex = Assert.Throws<InvalidOperationException>(
            () => OutboundPhiGuard.AssertAllowed("/api/agent/heartbeat", body, Options));

        Assert.Contains("field: note", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Allows_clean_control_plane_telemetry()
    {
        // The strings a heartbeat legitimately carries: ids, enums, a semver, a
        // hash, and ISO timestamps — all operational-safe, none PHI-shaped.
        var body = @"{
            ""agentId"":""a1b2c3"",
            ""status"":""online"",
            ""version"":""3.15.4"",
            ""machineFingerprint"":""a3f29c5e8842d10b4f7c9e2a"",
            ""lastVerifiedAt"":""2026-05-30T02:14:27.493-07:00"",
            ""runtimeHealth"":{
                ""component"":""cloud_sync"",
                ""status"":""healthy"",
                ""lastSuccessAt"":""2026-05-30T02:13:11.004-07:00""
            }
        }";

        var ex = Record.Exception(
            () => OutboundPhiGuard.AssertAllowed("/api/agent/heartbeat", body, Options));

        Assert.Null(ex);
    }
}
