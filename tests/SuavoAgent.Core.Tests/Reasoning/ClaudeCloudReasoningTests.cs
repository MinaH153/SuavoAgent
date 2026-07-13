using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Contracts.Behavioral;
using SuavoAgent.Contracts.Reasoning;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Reasoning;
using Xunit;

namespace SuavoAgent.Core.Tests.Reasoning;

public sealed class ClaudeCloudReasoningTests
{
    private static readonly string AgentId = Guid.NewGuid().ToString("D");
    private static readonly string PharmacyId = Guid.NewGuid().ToString("D");

    [Fact]
    public void BuildScrubbedPayload_IsExactBoundedAndPhiNegative()
    {
        var request = Request(new HashSet<RuleActionType>
        {
            RuleActionType.VerifyElement,
            RuleActionType.Log,
        });
        var requestId = Guid.NewGuid();

        var payload = ClaudeCloudReasoning.BuildScrubbedPayload(
            request, "local_model_no_proposal", requestId);
        var json = JsonSerializer.Serialize(payload);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(
            new[]
            {
                "allowedActions", "escalationCode", "requestId",
                "schemaVersion", "scrubbedState", "skillId",
            },
            root.EnumerateObject().Select(property => property.Name)
                .Order(StringComparer.Ordinal));
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(requestId.ToString("D"), root.GetProperty("requestId").GetString());
        Assert.Equal(
            ["Log", "VerifyElement"],
            root.GetProperty("allowedActions").EnumerateArray()
                .Select(value => value.GetString()));
        Assert.Equal(
            payload.AllowedActions.Order(StringComparer.Ordinal),
            payload.AllowedActions);
        Assert.DoesNotContain("John Smith", json, StringComparison.Ordinal);
        Assert.DoesNotContain("01/15/1990", json, StringComparison.Ordinal);
        Assert.DoesNotContain("555-123-4567", json, StringComparison.Ordinal);
        Assert.DoesNotContain("123 Main Street", json, StringComparison.Ordinal);
        Assert.DoesNotContain("998877", json, StringComparison.Ordinal);
        var state = root.GetProperty("scrubbedState");
        Assert.Equal(
            new[]
            {
                "flags", "operatorIdleMs", "processName", "visibleElements",
                "windowTitle",
            },
            state.EnumerateObject().Select(property => property.Name)
                .Order(StringComparer.Ordinal));
        Assert.Equal("", state.GetProperty("windowTitle").GetString());
        Assert.DoesNotContain("userObjective", state.EnumerateObject()
            .Select(property => property.Name));
        Assert.All(
            state.GetProperty("visibleElements").EnumerateArray(),
            element => Assert.Matches(
                "^(Button|Edit):[0-9a-f]{64}:[0-9a-f]{2}$",
                element.GetString()!));
        Assert.Equal(
            payload.ScrubbedState.VisibleElements.Order(StringComparer.Ordinal),
            payload.ScrubbedState.VisibleElements);
        Assert.Equal(
            new[] { "e0", "f8" },
            state.GetProperty("visibleElements").EnumerateArray()
                .Select(element => element.GetString()![^2..])
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public void StructuralFingerprint_UsesExactRfc8785IdentityAndObservedState()
    {
        var request = Request() with
        {
            Context = Request().Context with
            {
                ElementFingerprints =
                [
                    new ElementSignature("Button", "btnSave", "WpfButton"),
                ],
                StructuralElementStates =
                [
                    new StructuralElementObservation(
                        new ElementSignature("Button", "btnSave", "WpfButton"),
                        0x03),
                ],
                CloudStructuralStateEligible = true,
            },
        };
        var payload = ClaudeCloudReasoning.BuildScrubbedPayload(
            request, "local_model_no_proposal", Guid.NewGuid());

        Assert.Equal(
            "Button:9cf51d3b17eb76ebb0c700a431953b85158f1240c8a692599f897e0ba881818e:03",
            Assert.Single(payload.ScrubbedState.VisibleElements));
    }

    [Fact]
    public void ComputeStateHash_MatchesWebGoldenRfc8785Vector()
    {
        var payload = new ClaudeCloudReasoning.ReasonRequest(
            1,
            "11111111-1111-4111-8111-111111111111",
            "pricing.verify",
            new ClaudeCloudReasoning.ReasonScrubbedState(
                "PioneerRx.exe",
                "",
                [
                    $"DataGrid:{new string('b', 64)}:01",
                    $"Button:{new string('a', 64)}:03",
                ],
                1250,
                new SortedDictionary<string, string>(StringComparer.Ordinal)
                {
                    ["riskTier"] = "green",
                    ["dialogState"] = "none",
                    ["screenClass"] = "pricing",
                },
                null),
            "local_model_low_confidence",
            ["WaitForElement", "Click"]);

        Assert.Equal(
            "2e47d67265ab6afadc2f70d1fa8f00bb00827c0a0d9bffafdfb294e6adccceae",
            ClaudeCloudReasoning.ComputeStateHash(payload));
        var reordered = payload with
        {
            AllowedActions = payload.AllowedActions.Reverse().ToArray(),
            ScrubbedState = payload.ScrubbedState with
            {
                VisibleElements = payload.ScrubbedState.VisibleElements
                    .Reverse()
                    .ToArray(),
            },
        };
        Assert.Equal(
            ClaudeCloudReasoning.ComputeStateHash(payload),
            ClaudeCloudReasoning.ComputeStateHash(reordered));
    }

    [Fact]
    public async Task ProposeAsync_ExactBoundSignedReceipt_ReturnsProposal()
    {
        var signer = new ReasonReceiptSigner(AgentId, PharmacyId);
        var sut = Sut(signer);

        var proposal = await sut.ProposeAsync(
            Request(), "local_model_no_proposal", CancellationToken.None);

        Assert.NotNull(proposal);
        Assert.Equal(RuleActionType.VerifyElement, proposal!.Action.Type);
        Assert.Equal("Save", proposal.Action.Parameters["name"]);
        Assert.Equal(0.87, proposal.Confidence);
        Assert.Equal("claude-sonnet-4-6", proposal.ModelId);
        Assert.Equal(InferenceRationaleCode.TargetPresent, proposal.RationaleCode);
        Assert.Equal(120, proposal.LatencyMs);
        Assert.Single(signer.Payloads);
        var sent = Assert.IsType<ClaudeCloudReasoning.ReasonRequest>(
            signer.Payloads[0]);
        Assert.True(IsUuidV4(Guid.Parse(sent.RequestId)));
    }

    [Theory]
    [InlineData("target_present", InferenceRationaleCode.TargetPresent)]
    [InlineData("target_absent_wait", InferenceRationaleCode.TargetAbsentWait)]
    [InlineData("workflow_state_ambiguous", InferenceRationaleCode.WorkflowStateAmbiguous)]
    [InlineData("operator_input_required", InferenceRationaleCode.OperatorInputRequired)]
    [InlineData("verification_required", InferenceRationaleCode.VerificationRequired)]
    [InlineData("recovery_step_required", InferenceRationaleCode.RecoveryStepRequired)]
    [InlineData("no_safe_action", InferenceRationaleCode.NoSafeAction)]
    public async Task ProposeAsync_AcceptsOnlyExactRationaleCodeVocabulary(
        string wireValue,
        InferenceRationaleCode expected)
    {
        var signer = new ReasonReceiptSigner(AgentId, PharmacyId)
        {
            RationaleCode = wireValue,
        };

        var proposal = await Sut(signer).ProposeAsync(
            Request(), "local_model_no_proposal", CancellationToken.None);

        Assert.NotNull(proposal);
        Assert.Equal(expected, proposal.RationaleCode);
        Assert.Equal(wireValue, proposal.RationaleCode.ToWireValue());
    }

    [Theory]
    [InlineData(ReceiptMode.UnknownTopLevelField)]
    [InlineData(ReceiptMode.ReplayedRequest)]
    [InlineData(ReceiptMode.CrossAgent)]
    [InlineData(ReceiptMode.CrossPharmacy)]
    [InlineData(ReceiptMode.ActionInjection)]
    [InlineData(ReceiptMode.ActionWrongCase)]
    [InlineData(ReceiptMode.NonStringParameter)]
    [InlineData(ReceiptMode.UnknownActionField)]
    [InlineData(ReceiptMode.MissingRequiredActionParameter)]
    [InlineData(ReceiptMode.ExtraActionParameter)]
    [InlineData(ReceiptMode.EmptyRequiredActionParameter)]
    [InlineData(ReceiptMode.WrongStateHash)]
    [InlineData(ReceiptMode.WrongAuditReceiptVersion)]
    [InlineData(ReceiptMode.MismatchedAuditReceiptId)]
    [InlineData(ReceiptMode.ConfidenceOutOfRange)]
    [InlineData(ReceiptMode.MissingCached)]
    [InlineData(ReceiptMode.WrongModel)]
    [InlineData(ReceiptMode.WrongProvider)]
    [InlineData(ReceiptMode.FreeTextRationaleCode)]
    [InlineData(ReceiptMode.WrongCaseRationaleCode)]
    [InlineData(ReceiptMode.LegacyRationaleField)]
    [InlineData(ReceiptMode.MissingRationaleCode)]
    [InlineData(ReceiptMode.PhiParameter)]
    [InlineData(ReceiptMode.BadBodyDigest)]
    [InlineData(ReceiptMode.BadKeyId)]
    [InlineData(ReceiptMode.BadSignatureShape)]
    [InlineData(ReceiptMode.OversizedBody)]
    public async Task ProposeAsync_UntrustedOrUnboundResponse_FailsClosed(
        ReceiptMode mode)
    {
        var sut = Sut(new ReasonReceiptSigner(AgentId, PharmacyId) { Mode = mode });

        var proposal = await sut.ProposeAsync(
            Request(), "local_model_no_proposal", CancellationToken.None);

        Assert.Null(proposal);
    }

    [Fact]
    public async Task ProposeAsync_UnsignedResponse_FailsClosed()
    {
        var sut = Sut(new ReasonReceiptSigner(AgentId, PharmacyId)
        {
            Mode = ReceiptMode.Unsigned,
        });

        Assert.Null(await sut.ProposeAsync(
            Request(), "local_model_no_proposal", CancellationToken.None));
    }

    [Theory]
    [InlineData(400, true, "reasoning_invalid")]
    [InlineData(400, true, "reasoning_phi_boundary_violation")]
    [InlineData(401, true, "reasoning_unauthorized")]
    [InlineData(403, true, "reasoning_agent_binding_invalid")]
    [InlineData(409, true, "reasoning_request_conflict")]
    [InlineData(409, false, "reasoning_request_in_progress")]
    [InlineData(412, false, "reasoning_pharmacy_baa_required")]
    [InlineData(412, false, "reasoning_anthropic_baa_required")]
    [InlineData(429, false, "reasoning_quota_exceeded")]
    [InlineData(502, false, "reasoning_provider_unavailable")]
    [InlineData(502, false, "reasoning_proposal_invalid")]
    [InlineData(502, false, "reasoning_proposal_action_not_allowed")]
    [InlineData(502, false, "reasoning_proposal_phi_boundary_violation")]
    [InlineData(503, false, "reasoning_preflight_unavailable")]
    [InlineData(503, false, "reasoning_anthropic_org_unavailable")]
    [InlineData(503, false, "reasoning_cache_invalid")]
    [InlineData(503, false, "reasoning_receipt_unavailable")]
    public void TryParseRejection_OnlyAcceptsExactStatusTerminalCodeTuple(
        int status,
        bool terminal,
        string code)
    {
        var response = SignedResponse(status, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            kind = "reasoning_rejection",
            accepted = false,
            terminal,
            code,
        }));

        Assert.True(ClaudeCloudReasoning.TryParseRejection(
            response, out var parsedTerminal, out var parsedCode));
        Assert.Equal(terminal, parsedTerminal);
        Assert.Equal(code, parsedCode);
    }

    [Theory]
    [InlineData(400, false, "reasoning_invalid")]
    [InlineData(409, true, "reasoning_request_in_progress")]
    [InlineData(503, false, "response_signing_unavailable")]
    [InlineData(502, true, "reasoning_provider_unavailable")]
    public void TryParseRejection_MismatchedTupleIsRejected(
        int status,
        bool terminal,
        string code)
    {
        var response = SignedResponse(status, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            kind = "reasoning_rejection",
            accepted = false,
            terminal,
            code,
        }));

        Assert.False(ClaudeCloudReasoning.TryParseRejection(
            response, out _, out _));
    }

    [Theory]
    [InlineData("legacy prose")]
    [InlineData("local_model_unknown")]
    public async Task ProposeAsync_UnknownEscalationNeverReachesNetwork(string code)
    {
        var signer = new ReasonReceiptSigner(AgentId, PharmacyId);
        var sut = Sut(signer);

        Assert.Null(await sut.ProposeAsync(Request(), code, CancellationToken.None));
        Assert.Empty(signer.Payloads);
    }

    [Fact]
    public async Task ProposeAsync_InvalidStateNeverReachesNetwork()
    {
        var signer = new ReasonReceiptSigner(AgentId, PharmacyId);
        var request = Request() with
        {
            Context = Request().Context with
            {
                Flags = new Dictionary<string, string> { ["not-valid-key!"] = "x" },
            },
        };

        Assert.Null(await Sut(signer).ProposeAsync(
            request, "local_model_error", CancellationToken.None));
        Assert.Empty(signer.Payloads);
    }

    [Fact]
    public async Task ProposeAsync_IncompleteStructuralStateNeverReachesNetwork()
    {
        var signer = new ReasonReceiptSigner(AgentId, PharmacyId);
        var request = Request() with
        {
            Context = Request().Context with
            {
                CloudStructuralStateEligible = false,
            },
        };

        Assert.Null(await Sut(signer).ProposeAsync(
            request, "local_model_error", CancellationToken.None));
        Assert.Empty(signer.Payloads);
    }

    [Fact]
    public async Task ProposeAsync_CallerCancellationPropagates()
    {
        var signer = new ReasonReceiptSigner(AgentId, PharmacyId)
        {
            Throw = new OperationCanceledException(),
        };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            Sut(signer).ProposeAsync(
                Request(), "local_model_timeout", cts.Token));
    }

    private static ClaudeCloudReasoning Sut(IPostSigner signer) =>
        new(
            signer,
            new AgentOptions { AgentId = AgentId, PharmacyId = PharmacyId },
            NullLogger<ClaudeCloudReasoning>.Instance);

    private static InferenceRequest Request(
        IReadOnlySet<RuleActionType>? allowedActions = null) =>
        new()
        {
            Context = new RuleContext
            {
                SkillId = "pricing-lookup",
                ProcessName = "PioneerPharmacy",
                WindowTitle = "Patient: John Smith - Rx Lookup",
                VisibleElements = new HashSet<string>
                {
                    "Save",
                    "DOB: 01/15/1990",
                    "123 Main Street",
                },
                ElementFingerprints = new[]
                {
                    new ElementSignature("Button", "btnSave", "WinButton"),
                    new ElementSignature("Edit", "rxLookup", null),
                },
                StructuralElementStates = new[]
                {
                    new StructuralElementObservation(
                        new ElementSignature("Button", "btnSave", "WinButton"),
                        0xF8),
                    new StructuralElementObservation(
                        new ElementSignature("Edit", "rxLookup", null),
                        0xE0),
                },
                CloudStructuralStateEligible = true,
                OperatorIdleMs = 42,
                Flags = new Dictionary<string, string>
                {
                    ["dialogState"] = "none",
                    ["riskTier"] = "green",
                    ["screenClass"] = "pricing",
                },
                UserObjective = "Call 555-123-4567 about Rx 998877 for John Smith",
            },
            EscalationReason = "no rule matched",
            AllowedActions = allowedActions ?? new HashSet<RuleActionType>
            {
                RuleActionType.VerifyElement,
                RuleActionType.Log,
            },
        };

    private static VerifiedCloudPostResponse SignedResponse(
        int status,
        string body,
        string? digest = null,
        string keyId = "suavo-cmd-v1",
        byte[]? signature = null) =>
        new(
            status,
            body,
            digest ?? Sha256(body),
            keyId,
            Convert.ToBase64String(signature ?? new byte[64]));

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static bool IsUuidV4(Guid value)
    {
        var text = value.ToString("D");
        return text[14] == '4' && text[19] is '8' or '9' or 'a' or 'b';
    }

    public enum ReceiptMode
    {
        Valid,
        Unsigned,
        UnknownTopLevelField,
        ReplayedRequest,
        CrossAgent,
        CrossPharmacy,
        ActionInjection,
        ActionWrongCase,
        NonStringParameter,
        UnknownActionField,
        MissingRequiredActionParameter,
        ExtraActionParameter,
        EmptyRequiredActionParameter,
        WrongStateHash,
        WrongAuditReceiptVersion,
        MismatchedAuditReceiptId,
        ConfidenceOutOfRange,
        MissingCached,
        WrongModel,
        WrongProvider,
        FreeTextRationaleCode,
        WrongCaseRationaleCode,
        LegacyRationaleField,
        MissingRationaleCode,
        PhiParameter,
        BadBodyDigest,
        BadKeyId,
        BadSignatureShape,
        OversizedBody,
    }

    private sealed class ReasonReceiptSigner : IPostSigner
    {
        private readonly string _agentId;
        private readonly string _pharmacyId;

        internal ReasonReceiptSigner(string agentId, string pharmacyId)
        {
            _agentId = agentId;
            _pharmacyId = pharmacyId;
        }

        internal ReceiptMode Mode { get; init; }
        internal string RationaleCode { get; init; } = "target_present";
        internal Exception? Throw { get; init; }
        internal List<object> Payloads { get; } = [];

        public Task<JsonElement?> PostSignedAsync(
            string path, object payload, CancellationToken ct) =>
            throw new InvalidOperationException("unsigned reasoning path used");

        public Task<JsonElement?> PostSignedVerifiedAsync(
            string path,
            object payload,
            string publicKeyDer,
            CancellationToken ct) =>
            throw new InvalidOperationException("legacy verified path used");

        public Task<VerifiedCloudPostResponse?> PostSignedResponseVerifiedAsync(
            string path,
            object payload,
            CancellationToken ct)
        {
            if (Throw is not null) throw Throw;
            Payloads.Add(payload);
            if (Mode == ReceiptMode.Unsigned)
                return Task.FromResult<VerifiedCloudPostResponse?>(null);
            Assert.Equal("/api/agent/reason", path);
            var request = Assert.IsType<ClaudeCloudReasoning.ReasonRequest>(payload);
            var top = new Dictionary<string, object?>
            {
                ["schemaVersion"] = 1,
                ["kind"] = "reasoning_proposal_receipt",
                ["requestId"] = Mode == ReceiptMode.ReplayedRequest
                    ? Guid.NewGuid().ToString("D") : request.RequestId,
                ["agentInstanceId"] = Mode == ReceiptMode.CrossAgent
                    ? Guid.NewGuid().ToString("D") : _agentId,
                ["pharmacyId"] = Mode == ReceiptMode.CrossPharmacy
                    ? Guid.NewGuid().ToString("D") : _pharmacyId,
                ["stateHash"] = Mode == ReceiptMode.WrongStateHash
                    ? new string('0', 64)
                    : ClaudeCloudReasoning.ComputeStateHash(request),
                ["auditReceiptId"] = Mode switch
                {
                    ReceiptMode.WrongAuditReceiptVersion =>
                        "00000000-0000-1000-8000-000000000000",
                    ReceiptMode.MismatchedAuditReceiptId =>
                        Guid.NewGuid().ToString("D"),
                    _ => request.RequestId,
                },
                ["action"] = Action(),
                ["confidence"] = Mode == ReceiptMode.ConfidenceOutOfRange ? 1.7 : 0.87,
                ["rationaleCode"] = Mode switch
                {
                    ReceiptMode.FreeTextRationaleCode =>
                        "Patient: John Smith needs a prescription",
                    ReceiptMode.WrongCaseRationaleCode => "Target_Present",
                    ReceiptMode.OversizedBody => new string('x', 17_000),
                    _ => RationaleCode,
                },
                ["modelId"] = Mode == ReceiptMode.WrongModel
                    ? "unknown-model" : "claude-sonnet-4-6",
                ["providerId"] = Mode == ReceiptMode.WrongProvider
                    ? "claude-sonnet-4-6" : "anthropic",
                ["cached"] = false,
                ["latencyMs"] = 120,
            };
            if (Mode == ReceiptMode.UnknownTopLevelField)
                top["unexpected"] = true;
            if (Mode == ReceiptMode.MissingCached)
                top.Remove("cached");
            if (Mode == ReceiptMode.MissingRationaleCode)
                top.Remove("rationaleCode");
            if (Mode == ReceiptMode.LegacyRationaleField)
            {
                top.Remove("rationaleCode");
                top["rationale"] = "Patient: John Smith needs a prescription";
            }
            var body = JsonSerializer.Serialize(top);
            var response = SignedResponse(
                200,
                body,
                Mode == ReceiptMode.BadBodyDigest ? new string('0', 64) : null,
                Mode == ReceiptMode.BadKeyId ? "wrong-key" : "suavo-cmd-v1",
                Mode == ReceiptMode.BadSignatureShape ? new byte[63] : null);
            return Task.FromResult<VerifiedCloudPostResponse?>(response);
        }

        private object Action()
        {
            object parameters = Mode switch
            {
                ReceiptMode.NonStringParameter =>
                    new Dictionary<string, object?> { ["name"] = 3 },
                ReceiptMode.PhiParameter =>
                    new Dictionary<string, object?>
                    {
                        ["name"] = "Patient: John Smith DOB 01/15/1990",
                    },
                ReceiptMode.MissingRequiredActionParameter =>
                    new Dictionary<string, object?>(),
                ReceiptMode.ExtraActionParameter =>
                    new Dictionary<string, object?>
                    {
                        ["name"] = "Save",
                        ["unexpected"] = "x",
                    },
                ReceiptMode.EmptyRequiredActionParameter =>
                    new Dictionary<string, object?> { ["name"] = "  " },
                _ => new Dictionary<string, object?> { ["name"] = "Save" },
            };
            var action = new Dictionary<string, object?>
            {
                ["type"] = Mode switch
                {
                    ReceiptMode.ActionInjection => "Click",
                    ReceiptMode.ActionWrongCase => "verifyelement",
                    _ => "VerifyElement",
                },
                ["parameters"] = parameters,
            };
            if (Mode == ReceiptMode.UnknownActionField)
                action["unexpected"] = true;
            return action;
        }
    }
}
