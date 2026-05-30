using System;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using Xunit;

namespace SuavoAgent.Core.Tests.Cloud;

/// <summary>
/// The heartbeat is agent-built control-plane telemetry. Its fuzzy value scan
/// false-positived on the ISO timestamps, install paths, and operator consent
/// name it legitimately carries — blocking EVERY heartbeat, and with it command
/// delivery + online status. Control-plane paths keep the structural
/// blocked-field-NAME guard but skip the fuzzy value heuristic; data-sync paths
/// keep both.
/// </summary>
public sealed class OutboundPhiGuardTests
{
    private static readonly AgentOptions Options = new();

    [Fact]
    public void Heartbeat_allows_telemetry_that_trips_the_fuzzy_value_scan()
    {
        // A timestamp (safe), an install path, and a value with a phone-shaped
        // number — the kind of strings a heartbeat legitimately carries.
        var body = @"{
            ""agentId"":""a"",
            ""lastVerifiedAt"":""2026-05-30T02:14:27.493-07:00"",
            ""installPath"":""C:\\Program Files\\Suavo\\Agent"",
            ""reason"":""operator follow-up call 555-123-4567 pending""
        }";

        var ex = Record.Exception(
            () => OutboundPhiGuard.AssertAllowed("/api/agent/heartbeat", body, Options));

        Assert.Null(ex);
    }

    [Fact]
    public void Heartbeat_still_blocks_an_actual_patient_field_name()
    {
        // Structural field-name guard is kept even on control-plane paths.
        var body = @"{ ""patientName"": ""x"" }";

        Assert.Throws<InvalidOperationException>(
            () => OutboundPhiGuard.AssertAllowed("/api/agent/heartbeat", body, Options));
    }

    [Fact]
    public void Data_path_still_blocks_phi_in_a_value()
    {
        var body = @"{ ""note"": ""follow-up call 555-123-4567 today"" }";

        Assert.Throws<InvalidOperationException>(
            () => OutboundPhiGuard.AssertAllowed("/api/agent/some-data", body, Options));
    }
}
