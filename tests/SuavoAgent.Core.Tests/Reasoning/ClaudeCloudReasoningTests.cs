using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Contracts.Reasoning;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Reasoning;
using Xunit;

namespace SuavoAgent.Core.Tests.Reasoning;

public class ClaudeCloudReasoningTests
{
    // --- ParseProposal ------------------------------------------------------

    [Fact]
    public void ParseProposal_ValidResponse_ReturnsProposal()
    {
        var json = JsonDocument.Parse("""
        {
          "success": true,
          "data": {
            "action": { "type": "VerifyElement", "parameters": { "name": "Save" } },
            "confidence": 0.87,
            "rationale": "Standard verification of known control.",
            "modelId": "claude-sonnet-4-6",
            "cached": false,
            "latencyMs": 120
          }
        }
        """);

        var p = ClaudeCloudReasoning.ParseProposal(json.RootElement, 100);

        Assert.NotNull(p);
        Assert.Equal(RuleActionType.VerifyElement, p!.Action.Type);
        Assert.Equal("Save", p.Action.Parameters["name"]);
        Assert.Equal(0.87, p.Confidence);
        Assert.Equal("claude-sonnet-4-6", p.ModelId);
        Assert.Equal("Standard verification of known control.", p.Rationale);
    }

    [Fact]
    public void ParseProposal_SuccessFalse_ReturnsNull()
    {
        var json = JsonDocument.Parse("""{"success": false, "error": "rate-limited"}""");
        Assert.Null(ClaudeCloudReasoning.ParseProposal(json.RootElement, 0));
    }

    [Fact]
    public void ParseProposal_UnknownActionType_ReturnsNull()
    {
        var json = JsonDocument.Parse("""
        { "success": true,
          "data": { "action": { "type": "ExplodeSystem", "parameters": {} },
                    "confidence": 0.9 } }
        """);

        Assert.Null(ClaudeCloudReasoning.ParseProposal(json.RootElement, 0));
    }

    [Fact]
    public void ParseProposal_MissingConfidence_DefaultsSafe()
    {
        var json = JsonDocument.Parse("""
        { "success": true,
          "data": { "action": { "type": "Log", "parameters": {} } } }
        """);

        var p = ClaudeCloudReasoning.ParseProposal(json.RootElement, 0);

        Assert.NotNull(p);
        Assert.Equal(0.5, p!.Confidence);
    }

    [Fact]
    public void ParseProposal_ConfidenceOutOfRange_Clamps()
    {
        var json = JsonDocument.Parse("""
        { "success": true,
          "data": { "action": { "type": "VerifyElement", "parameters": {} },
                    "confidence": 1.7 } }
        """);

        var p = ClaudeCloudReasoning.ParseProposal(json.RootElement, 0);

        Assert.Equal(1.0, p!.Confidence);
    }

    [Fact]
    public void ParseProposal_NonStringParamValue_Stringified()
    {
        var json = JsonDocument.Parse("""
        { "success": true,
          "data": { "action": { "type": "VerifyElement",
                    "parameters": { "retries": 3, "strict": true } },
                    "confidence": 0.9 } }
        """);

        var p = ClaudeCloudReasoning.ParseProposal(json.RootElement, 0);

        Assert.Equal("3", p!.Action.Parameters["retries"]);
        Assert.Equal("true", p.Action.Parameters["strict"]);
    }

    // --- BuildScrubbedPayload ------------------------------------------------

    [Fact]
    public void BuildScrubbedPayload_ScrubsWindowTitleAndElements()
    {
        var ctx = new RuleContext
        {
            SkillId = "pricing-lookup",
            ProcessName = "PioneerPharmacy",
            WindowTitle = "Patient: John Smith — Rx Lookup",
            VisibleElements = new HashSet<string>
            {
                "Save",
                "DOB: 01/15/1990",
            },
            Flags = new Dictionary<string, string>
            {
                ["on_call"] = "Call from 555-123-4567",
            },
        };
        var req = new InferenceRequest
        {
            Context = ctx,
            EscalationReason = "Patient: Jane Doe had no rule match",
            AllowedActions = InferenceRequest.SafeDefault,
        };

        var payload = ClaudeCloudReasoning.BuildScrubbedPayload(req, req.EscalationReason);
        var json = JsonSerializer.Serialize(payload);

        // No raw PHI.
        Assert.DoesNotContain("John Smith", json);
        Assert.DoesNotContain("Jane Doe", json);
        Assert.DoesNotContain("01/15/1990", json);
        Assert.DoesNotContain("555-123-4567", json);

        // Non-PHI preserved.
        Assert.Contains("PioneerPharmacy", json);
        Assert.Contains("Save", json);
        Assert.Contains("pricing-lookup", json);
    }

    [Fact]
    public void BuildScrubbedPayload_AllowedActionsSerialized()
    {
        var req = new InferenceRequest
        {
            Context = new RuleContext { SkillId = "s1" },
            EscalationReason = "test",
            AllowedActions = new HashSet<RuleActionType>
            {
                RuleActionType.VerifyElement,
                RuleActionType.Log,
            },
        };

        var payload = ClaudeCloudReasoning.BuildScrubbedPayload(req, req.EscalationReason);
        var json = JsonSerializer.Serialize(payload);

        Assert.Contains("VerifyElement", json);
        Assert.Contains("Log", json);
    }

    // --- ProposeAsync (with stub signer) ------------------------------------

    [Fact]
    public async Task ProposeAsync_SignerReturnsValidBody_ReturnsProposal()
    {
        var signer = new StubPostSigner(JsonDocument.Parse("""
        { "success": true,
          "data": { "action": { "type": "VerifyElement", "parameters": { "name": "Save" } },
                    "confidence": 0.9, "modelId": "claude-sonnet-4-6" } }
        """).RootElement);

        var sut = new ClaudeCloudReasoning(signer, NullLogger<ClaudeCloudReasoning>.Instance);

        var req = new InferenceRequest
        {
            Context = new RuleContext { SkillId = "s1" },
            EscalationReason = "tier-2 failed",
        };

        var proposal = await sut.ProposeAsync(req, "tier-2 failed", CancellationToken.None);

        Assert.NotNull(proposal);
        Assert.Equal(RuleActionType.VerifyElement, proposal!.Action.Type);
    }

    [Fact]
    public async Task ProposeAsync_SignerThrows_ReturnsNull()
    {
        var signer = new StubPostSigner(throwEx: new HttpRequestException("nope"));
        var sut = new ClaudeCloudReasoning(signer, NullLogger<ClaudeCloudReasoning>.Instance);

        var req = new InferenceRequest
        {
            Context = new RuleContext { SkillId = "s1" },
            EscalationReason = "x",
        };

        var proposal = await sut.ProposeAsync(req, "x", CancellationToken.None);

        Assert.Null(proposal);
    }

    [Fact]
    public async Task ProposeAsync_NullResponseBody_ReturnsNull()
    {
        var signer = new StubPostSigner(null);
        var sut = new ClaudeCloudReasoning(signer, NullLogger<ClaudeCloudReasoning>.Instance);

        var req = new InferenceRequest
        {
            Context = new RuleContext { SkillId = "s1" },
            EscalationReason = "x",
        };

        var proposal = await sut.ProposeAsync(req, "x", CancellationToken.None);

        Assert.Null(proposal);
    }

    [Fact]
    public async Task ProposeAsync_Cancelled_Propagates()
    {
        var signer = new StubPostSigner(throwEx: new OperationCanceledException());
        var sut = new ClaudeCloudReasoning(signer, NullLogger<ClaudeCloudReasoning>.Instance);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var req = new InferenceRequest
        {
            Context = new RuleContext { SkillId = "s1" },
            EscalationReason = "x",
        };

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            sut.ProposeAsync(req, "x", cts.Token));
    }

    // --- helpers ------------------------------------------------------------

    private sealed class StubPostSigner : IPostSigner
    {
        private readonly JsonElement? _response;
        private readonly Exception? _throw;

        public StubPostSigner(JsonElement? response = null, Exception? throwEx = null)
        {
            _response = response;
            _throw = throwEx;
        }

        public Task<JsonElement?> PostSignedAsync(string path, object payload, CancellationToken ct)
        {
            if (_throw != null) throw _throw;
            return Task.FromResult(_response);
        }

        public Task<JsonElement?> PostSignedVerifiedAsync(
            string path, object payload, string publicKeyDer, CancellationToken ct) =>
            throw new NotImplementedException();
    }
}
