using System;
using System.Text.Json;
using SuavoAgent.Helper.Actuation;
using Xunit;

namespace SuavoAgent.Helper.Tests.Actuation;

/// <summary>QA C4: the fail-closed BAA-scope gate that gates every PioneerRx actuation verb.</summary>
public class PioneerRxBaaScopeTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;
    private static readonly string[] Allowed = { "BaaAmendment", "DeliveryWriteback" };

    [Fact]
    public void Allowlisted_scope_is_authorized()
        => Assert.True(PioneerRxCommandHandler.IsBaaScopeAuthorized(
            Json("{\"baaScopeTag\":\"BaaAmendment\"}"), Allowed, out _));

    [Fact]
    public void Unlisted_scope_is_rejected()
    {
        Assert.False(PioneerRxCommandHandler.IsBaaScopeAuthorized(
            Json("{\"baaScopeTag\":\"SomethingElse\"}"), Allowed, out var reason));
        Assert.Contains("not authorized", reason);
    }

    [Fact]
    public void Missing_tag_is_rejected_fail_closed()
        => Assert.False(PioneerRxCommandHandler.IsBaaScopeAuthorized(
            Json("{\"label\":\"ok-button\"}"), Allowed, out _));

    [Fact]
    public void Empty_or_whitespace_tag_is_rejected()
        => Assert.False(PioneerRxCommandHandler.IsBaaScopeAuthorized(
            Json("{\"baaScopeTag\":\"  \"}"), Allowed, out _));

    [Fact]
    public void Empty_allowlist_rejects_even_a_present_tag()
        => Assert.False(PioneerRxCommandHandler.IsBaaScopeAuthorized(
            Json("{\"baaScopeTag\":\"BaaAmendment\"}"), Array.Empty<string>(), out _));

    [Fact]
    public void Null_data_is_rejected()
        => Assert.False(PioneerRxCommandHandler.IsBaaScopeAuthorized(null, Allowed, out _));

    [Fact]
    public void Scope_match_is_ordinal_case_sensitive()
        => Assert.False(PioneerRxCommandHandler.IsBaaScopeAuthorized(
            Json("{\"baaScopeTag\":\"baaamendment\"}"), Allowed, out _));
}
