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

    [Fact]
    public void Allows_a_learning_session_id_on_the_heartbeat()
    {
        // The moat regression: LearningWorker mints sessionId = "learn-{agentId}-{yyyyMMddHHmmss}".
        // The 14-digit timestamp run contains a 10-digit substring that the Phone value-rule matches,
        // so once learning turned on EVERY heartbeat was hard-blocked (agent went offline). sessionId
        // is a machine-generated operational token — exempt by NAME (like evidenceid/scanwindowid),
        // never by weakening the Phone value pattern (see the companion test below).
        var body = @"{
            ""agentId"":""a1b2c3"",
            ""status"":""online"",
            ""templateLearning"":{
                ""enabled"":true,
                ""phase"":""discovery"",
                ""sessionId"":""learn-8dc472b9a1b2c3d4-20260704004322""
            }
        }";

        Assert.Null(Record.Exception(
            () => OutboundPhiGuard.AssertAllowed("/api/agent/heartbeat", body, Options)));
    }

    [Fact]
    public void Still_blocks_the_same_ten_digit_run_under_a_non_exempt_field()
    {
        // Proves the sessionId fix is a NAME exemption, not a neutered Phone rule: the identical
        // 10-digit run under a free-form field name must still block (phone/PHI egress protection).
        var body = @"{ ""freeNote"":""2026070400"" }";

        Assert.Throws<InvalidOperationException>(
            () => OutboundPhiGuard.AssertAllowed("/api/agent/heartbeat", body, Options));
    }

    [Fact]
    public void Allows_utc_iso_timestamps_with_a_positive_offset()
    {
        // The exact field that was blocking every heartbeat: canary.lastVerifiedAt is
        // DateTimeOffset.UtcNow.ToString("o") -> a "+00:00" offset. The "+" escapes the
        // operational charset (which allows "-" but not "+") and the embedded date trips
        // the PHI date pattern. Also covers the "Z" form and a non-whitelisted field name.
        var body = @"{
            ""canary"": { ""status"":""clean"", ""lastVerifiedAt"":""2026-05-30T10:00:59.6567122+00:00"" },
            ""watchdog"": { ""present"":true, ""generatedAtUtc"":""2026-05-30T10:00:59+00:00"" },
            ""lastVerifiedAt"":""2026-05-30T10:00:59.6567122Z""
        }";

        Assert.Null(Record.Exception(
            () => OutboundPhiGuard.AssertAllowed("/api/agent/heartbeat", body, Options)));
    }

    [Theory]
    [InlineData("patient dob 1990-01-15")] // bare ISO date — no T/time
    [InlineData("dob 01/15/1990")]          // US layout
    public void Still_blocks_a_date_of_birth_so_the_fix_does_not_weaken_phi(string dob)
    {
        var body = $@"{{ ""freeNote"":""{dob}"" }}";

        Assert.Throws<InvalidOperationException>(
            () => OutboundPhiGuard.AssertAllowed("/api/agent/heartbeat", body, Options));
    }

    [Theory]
    [InlineData("1990-01-15")]   // BARE ISO date-of-birth — was misclassified as an NDC token shape
    [InlineData("123-45-6789")]  // BARE SSN — was matched by the long-numeric operational shape
    public void Blocks_a_bare_charset_clean_phi_token_under_a_benign_field(string phi)
    {
        // These are single, charset-clean values (no surrounding words) that formerly classified as
        // OperationalSafe via the NDC (\d-\d-\d) / long-numeric (\d{6,}) token shapes and bypassed the
        // ContainsPhi value scan entirely — a DOB/SSN egress under a benign field name. The guard must
        // now block them by VALUE, not just by field name.
        var body = $@"{{ ""freeNote"":""{phi}"" }}";

        Assert.Throws<InvalidOperationException>(
            () => OutboundPhiGuard.AssertAllowed("/api/agent/heartbeat", body, Options));
    }

    [Fact]
    public void Still_allows_a_genuine_operational_token_after_narrowing_the_allowlist()
    {
        // The narrowing must not over-block real control-plane tokens: a hex fingerprint, a uuid,
        // a semver, and a snake_case enum are still operational-safe.
        var body = @"{
            ""fingerprint"":""a3f29c5e8842d10b4f7c9e2a"",
            ""commandId"":""3f2504e0-4f89-41d3-9a0c-0305e82c3301"",
            ""version"":""3.81.0"",
            ""reason"":""helper_unreachable""
        }";

        Assert.Null(Record.Exception(
            () => OutboundPhiGuard.AssertAllowed("/api/agent/heartbeat", body, Options)));
    }

    [Fact]
    public void Allows_a_pre_serialized_json_blob_field_with_embedded_timestamps()
    {
        // intelligenceContext / efficiencyReport / fleetSignals ship source-validated
        // JSON as a STRING value. Its embedded UTC timestamps must not trip the date
        // pattern when the blob is scanned as opaque text.
        var blob = @"{""channel"":""fleet-east"",""computedAt"":""2026-05-30T10:00:59.65+00:00"",""score"":42}";
        var body = $@"{{ ""fleetSignals"": {System.Text.Json.JsonSerializer.Serialize(blob)} }}";

        Assert.Null(Record.Exception(
            () => OutboundPhiGuard.AssertAllowed("/api/agent/heartbeat", body, Options)));
    }

    [Fact]
    public void Still_finds_phi_inside_a_json_blob_field_and_names_the_nested_field()
    {
        // Recursing into the blob keeps PHI detection: a patient phone hidden inside
        // intelligenceContext is still caught, named by its nested field.
        var blob = @"{""note"":""patient Jane Rivera 555-123-4567""}";
        var body = $@"{{ ""intelligenceContext"": {System.Text.Json.JsonSerializer.Serialize(blob)} }}";

        var ex = Assert.Throws<InvalidOperationException>(
            () => OutboundPhiGuard.AssertAllowed("/api/agent/heartbeat", body, Options));
        Assert.Contains("field: note", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
