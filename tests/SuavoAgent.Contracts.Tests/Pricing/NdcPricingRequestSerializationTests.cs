using System.Text.Json;
using SuavoAgent.Contracts.Behavioral;
using SuavoAgent.Contracts.Learning;
using SuavoAgent.Contracts.Pricing;
using Xunit;

namespace SuavoAgent.Contracts.Tests.Pricing;

/// <summary>
/// M2b-2: active selector patches ride <see cref="NdcPricingRequest"/> over the Core→Helper IPC
/// pipe as JSON. This guards that wire — patches (incl. the strict ElementSignature target +
/// fallbacks) must survive serialize→deserialize, and an old/empty request stays back-compatible.
/// </summary>
public class NdcPricingRequestSerializationTests
{
    [Fact]
    public void Patches_RoundTripThroughJson()
    {
        var req = new NdcPricingRequest("job1", 3, "00071015523", new[]
        {
            new SelectorPatch("p1", "skill-x", SelectorStepId.QuickSearchField, "v1.2", "scrA",
                new ElementSignature("Edit", "ndcSearchBox", "PioneerRxEdit"),
                new[] { new ElementSignature("Edit", "altSearchBox", null) },
                0.9, "seed-digest", 2),
        });

        var json = JsonSerializer.Serialize(req);
        var back = JsonSerializer.Deserialize<NdcPricingRequest>(json)!;

        Assert.Equal("00071015523", back.Ndc);
        var p = Assert.Single(back.Patches!);
        Assert.Equal("p1", p.PatchId);
        Assert.Equal(SelectorStepId.QuickSearchField, p.StepId);
        Assert.Equal("v1.2", p.PmsFingerprint);
        Assert.Equal("ndcSearchBox", p.Target.AutomationId);
        Assert.Equal("PioneerRxEdit", p.Target.ClassName);
        Assert.Equal("altSearchBox", Assert.Single(p.Fallbacks).AutomationId);
        Assert.Equal(2, p.Version);
    }

    [Fact]
    public void NoPatches_DefaultsNull_BackCompat()
    {
        // An older Helper / a request without patches must still deserialize cleanly.
        var json = JsonSerializer.Serialize(new NdcPricingRequest("j", 0, "x"));
        var back = JsonSerializer.Deserialize<NdcPricingRequest>(json)!;
        Assert.Null(back.Patches);
    }
}
