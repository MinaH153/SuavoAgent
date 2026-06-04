using System;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using Xunit;

namespace SuavoAgent.Core.Tests.Cloud;

/// <summary>
/// The enforced / shadow denylist split (blueprint fix #3 + #5). The outbound guard's enforced
/// behaviour is EXACTLY today's Core PHI patterns; the Diagnostics-origin staged rules only
/// LOG a would-block (shadow mode) so false-positives are measured on a real pilot before
/// promotion. These tests pin both halves.
/// </summary>
public sealed class OutboundPhiGuardShadowDenylistTests
{
    private static readonly AgentOptions Options = new();

    [Fact]
    public void Allows_a_staged_only_value_in_shadow_mode()
    {
        // A bare Windows user path is caught ONLY by the staged ShadowDenylist (Windows-user-path),
        // not the enforced Core set — so in default shadow mode it is allowed through (logged).
        var body = @"{ ""detail"": ""C:\\Users\\joshua\\AppData\\file.log"" }";
        Assert.Null(Record.Exception(
            () => OutboundPhiGuard.AssertAllowed("/api/agent/heartbeat", body, Options)));
    }

    [Fact]
    public void Still_blocks_an_enforced_phi_shape()
    {
        // A bare DOB is an ENFORCED shape (Core DatePattern) — the split must not weaken it.
        var body = @"{ ""freeNote"": ""patient dob 1990-01-15"" }";
        Assert.Throws<InvalidOperationException>(
            () => OutboundPhiGuard.AssertAllowed("/api/agent/heartbeat", body, Options));
    }
}
