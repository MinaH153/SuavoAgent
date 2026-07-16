using System.Collections.Frozen;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Contracts.Reasoning;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Diagnostics;
using CorePhiScrubber = SuavoAgent.Core.Learning.PhiScrubber;

namespace SuavoAgent.Core.Reasoning;

/// <summary>
/// Tier-3 cloud reasoning over an exact PHI-negative request and a pinned,
/// signed response. A response is advisory only after every request identity,
/// tenant identity, action, bound, and RFC 8785 state digest is verified.
/// </summary>
public sealed class ClaudeCloudReasoning : ICloudReasoning
{
    private const string Endpoint = "/api/agent/reason";
    private const string ExpectedModel = "claude-sonnet-4-6";
    private const string ExpectedProvider = "anthropic";
    private const int MaximumResponseBytes = 16 * 1024;
    private static readonly Regex SkillIdPattern = new(
        @"^[A-Za-z0-9][A-Za-z0-9._:-]{0,99}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ParameterKeyPattern = new(
        @"^[A-Za-z][A-Za-z0-9_]{0,31}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ProcessTokenPattern = new(
        @"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex StructuralComponentPattern = new(
        @"^[A-Za-z0-9][A-Za-z0-9_.:-]{0,63}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly FrozenSet<string> EscalationCodes = new[]
    {
        "local_model_unavailable", "local_model_no_proposal",
        "local_model_timeout", "local_model_error",
        "local_model_low_confidence",
    }.ToFrozenSet(StringComparer.Ordinal);
    private static readonly FrozenSet<string> StructuralRoles = new[]
    {
        "Button", "CheckBox", "ComboBox", "DataGrid", "DataItem",
        "Document", "Edit", "Group", "Header", "HeaderItem", "Hyperlink",
        "Image", "List", "ListItem", "Menu", "MenuBar", "MenuItem", "Pane",
        "ProgressBar", "RadioButton", "ScrollBar", "Separator", "Slider",
        "Spinner", "SplitButton", "StatusBar", "Tab", "TabItem", "Table",
        "Text", "Thumb", "TitleBar", "ToolBar", "ToolTip", "Tree",
        "TreeItem", "Window",
    }.ToFrozenSet(StringComparer.Ordinal);
    private static readonly FrozenDictionary<string, FrozenSet<string>> FlagValues =
        new Dictionary<string, FrozenSet<string>>(StringComparer.Ordinal)
        {
            ["screenClass"] = Set(
                "unknown", "login", "home", "queue", "search", "form",
                "grid", "dialog", "settings", "pricing"),
            ["focusClass"] = Set(
                "none", "button", "edit", "grid", "row", "cell", "menu",
                "tab", "dialog", "window", "other"),
            ["dialogState"] = Set("none", "open", "modal", "blocked"),
            ["networkState"] = Set("unknown", "online", "offline", "degraded"),
            ["inputMode"] = Set(
                "unknown", "keyboard", "pointer", "scanner", "touch"),
            ["workflowPhase"] = Set(
                "unknown", "observe", "locate", "verify", "act", "confirm",
                "recover"),
            ["riskTier"] = Set("green", "yellow", "red"),
            ["operatorPresence"] = Set("active", "idle", "away"),
        }.ToFrozenDictionary(StringComparer.Ordinal);
    private static readonly FrozenDictionary<string, RuleActionType> ActionTypes =
        Enum.GetValues<RuleActionType>()
            .ToFrozenDictionary(value => value.ToString(), value => value,
                StringComparer.Ordinal);

    private readonly IPostSigner _cloud;
    private readonly ILogger<ClaudeCloudReasoning> _logger;
    private readonly string _expectedAgentId;
    private readonly string _expectedPharmacyId;

    public ClaudeCloudReasoning(
        IPostSigner cloud,
        AgentOptions options,
        ILogger<ClaudeCloudReasoning> logger)
    {
        _cloud = cloud ?? throw new ArgumentNullException(nameof(cloud));
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _expectedAgentId = NormalizeUuidV4(options.AgentId, "agent");
        _expectedPharmacyId = NormalizeUuidV4(options.PharmacyId, "pharmacy");
    }

    public bool IsEnabled => true;

    public async Task<InferenceProposal?> ProposeAsync(
        InferenceRequest request,
        string tier2EscalationReason,
        CancellationToken ct)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(request);
            var requestId = Guid.NewGuid();
            var payload = BuildScrubbedPayload(
                request, tier2EscalationReason, requestId);
            var expectedStateHash = ComputeStateHash(payload);
            var response = await _cloud.PostSignedResponseVerifiedAsync(
                    Endpoint, payload, ct)
                .ConfigureAwait(false);
            if (response is null || !HasValidVerifiedEnvelope(response))
            {
                _logger.LogWarning("core.reasoning.receipt_untrusted");
                return null;
            }

            if (TryParseProposal(
                    response,
                    payload,
                    expectedStateHash,
                    _expectedAgentId,
                    _expectedPharmacyId,
                    out var proposal))
                return proposal;

            if (TryParseRejection(response, out var terminal, out var code))
            {
                _logger.LogWarning(
                    "core.reasoning.rejected code={Code} terminal={Terminal}",
                    code,
                    terminal);
                return null;
            }

            _logger.LogWarning("core.reasoning.receipt_contract_invalid");
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Fail closed. Safe logging records exception type only.
            _logger.LogSafeWarning(ex);
            return null;
        }
    }

    internal static ReasonRequest BuildScrubbedPayload(
        InferenceRequest request,
        string escalationCode,
        Guid requestId)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsUuidV4(requestId) ||
            !EscalationCodes.Contains(escalationCode) ||
            request.Context is null ||
            !SkillIdPattern.IsMatch(request.Context.SkillId))
            throw new InvalidDataException("reasoning_request_invalid");

        var allowedActions = NormalizeAllowedActions(request.AllowedActions);
        var context = request.Context;
        if (context.OperatorIdleMs < 0 ||
            !context.CloudStructuralStateEligible ||
            context.StructuralElementStates.Count is < 1 or > 8 ||
            context.Flags.Count > FlagValues.Count)
            throw new InvalidDataException("reasoning_state_invalid");

        if (!ProcessTokenPattern.IsMatch(context.ProcessName) ||
            !IsPhiNegative(context.ProcessName))
            throw new InvalidDataException("reasoning_state_invalid");
        var visibleElements = context.StructuralElementStates
            .Select(StructuralFingerprintToken)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (visibleElements.Distinct(StringComparer.Ordinal).Count() !=
            visibleElements.Length)
            throw new InvalidDataException("reasoning_state_invalid");
        var flags = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in context.Flags)
        {
            if (!FlagValues.TryGetValue(pair.Key, out var allowedValues) ||
                !allowedValues.Contains(pair.Value) ||
                !IsPhiNegative(pair.Value))
                throw new InvalidDataException("reasoning_state_invalid");
            flags.Add(pair.Key, pair.Value);
        }

        var state = new ReasonScrubbedState(
            context.ProcessName,
            "",
            visibleElements,
            context.OperatorIdleMs,
            flags,
            null);
        return new ReasonRequest(
            1,
            requestId.ToString("D"),
            context.SkillId,
            state,
            escalationCode,
            allowedActions);
    }

    internal static string ComputeStateHash(ReasonRequest request)
    {
        var normalizedState = request.ScrubbedState with
        {
            VisibleElements = request.ScrubbedState.VisibleElements
                .Order(StringComparer.Ordinal)
                .ToArray(),
        };
        var hashInput = new ReasonStateHashInput(
            request.SchemaVersion,
            request.SkillId,
            request.EscalationCode,
            request.AllowedActions.Order(StringComparer.Ordinal).ToArray(),
            normalizedState);
        var json = JsonSerializer.Serialize(hashInput);
        var canonical = Rfc8785Canonicalizer.CanonicalizeToUtf8(json);
        try
        {
            return Convert.ToHexString(SHA256.HashData(canonical))
                .ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonical);
        }
    }

    internal static bool TryParseProposal(
        VerifiedCloudPostResponse response,
        ReasonRequest request,
        string expectedStateHash,
        string expectedAgentId,
        string expectedPharmacyId,
        out InferenceProposal? proposal)
    {
        proposal = null;
        if (response.StatusCode != 200) return false;
        try
        {
            using var document = JsonDocument.Parse(response.Body);
            var root = document.RootElement;
            if (!HasExactKeys(root,
                    "schemaVersion", "kind", "requestId", "agentInstanceId",
                    "pharmacyId", "stateHash", "auditReceiptId", "action",
                    "confidence", "rationaleCode", "modelId", "providerId",
                    "cached", "latencyMs") ||
                !TryInt(root, "schemaVersion", out var schema) || schema != 1 ||
                !TryString(root, "kind", out var kind) ||
                    kind != "reasoning_proposal_receipt" ||
                !TryBoundUuid(root, "requestId", out var requestId) ||
                    requestId != request.RequestId ||
                !TryBoundUuid(root, "agentInstanceId", out var agentId) ||
                    agentId != expectedAgentId ||
                !TryBoundUuid(root, "pharmacyId", out var pharmacyId) ||
                    pharmacyId != expectedPharmacyId ||
                !TryString(root, "stateHash", out var stateHash) ||
                    stateHash != expectedStateHash ||
                !TryBoundUuid(root, "auditReceiptId", out var auditReceiptId) ||
                    auditReceiptId != request.RequestId ||
                !root.TryGetProperty("action", out var action) ||
                    !TryParseAction(action, request.AllowedActions, out var actionSpec) ||
                !TryFiniteDouble(root, "confidence", out var confidence) ||
                    confidence is < 0 or > 1 ||
                !TryString(root, "rationaleCode", out var rationaleCodeWire) ||
                !InferenceRationaleCodeCodec.TryParseWireValue(
                    rationaleCodeWire, out var rationaleCode) ||
                !TryString(root, "modelId", out var modelId) ||
                    modelId != ExpectedModel ||
                !TryString(root, "providerId", out var providerId) ||
                    providerId != ExpectedProvider ||
                !root.TryGetProperty("cached", out var cached) ||
                    cached.ValueKind is not (
                        JsonValueKind.True or JsonValueKind.False) ||
                !TryInt(root, "latencyMs", out var latencyMs) ||
                    latencyMs is < 0 or > 60_000)
                return false;

            proposal = new InferenceProposal
            {
                Action = actionSpec!,
                Confidence = confidence,
                ModelId = modelId,
                RationaleCode = rationaleCode,
                LatencyMs = latencyMs,
            };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool TryParseRejection(
        VerifiedCloudPostResponse response,
        out bool terminal,
        out string code)
    {
        terminal = false;
        code = "";
        try
        {
            using var document = JsonDocument.Parse(response.Body);
            var root = document.RootElement;
            if (!HasExactKeys(
                    root, "schemaVersion", "kind", "accepted", "terminal", "code") ||
                !TryInt(root, "schemaVersion", out var schema) || schema != 1 ||
                !TryString(root, "kind", out var kind) ||
                    kind != "reasoning_rejection" ||
                !root.TryGetProperty("accepted", out var accepted) ||
                    accepted.ValueKind != JsonValueKind.False ||
                !root.TryGetProperty("terminal", out var terminalElement) ||
                    terminalElement.ValueKind is not (
                        JsonValueKind.True or JsonValueKind.False) ||
                !TryString(root, "code", out code))
                return false;
            terminal = terminalElement.GetBoolean();
            return (response.StatusCode, terminal, code) switch
            {
                (400, true, "reasoning_invalid") => true,
                (400, true, "reasoning_phi_boundary_violation") => true,
                (401, true, "reasoning_unauthorized") => true,
                (403, true, "reasoning_agent_binding_invalid") => true,
                (409, true, "reasoning_request_conflict") => true,
                (409, false, "reasoning_request_in_progress") => true,
                (412, false, "reasoning_pharmacy_baa_required") => true,
                (412, false, "reasoning_anthropic_baa_required") => true,
                (429, false, "reasoning_quota_exceeded") => true,
                (502, false, "reasoning_provider_unavailable") => true,
                (502, false, "reasoning_proposal_invalid") => true,
                (502, false, "reasoning_proposal_action_not_allowed") => true,
                (502, false, "reasoning_proposal_phi_boundary_violation") => true,
                (503, false, "reasoning_preflight_unavailable") => true,
                (503, false, "reasoning_anthropic_org_unavailable") => true,
                (503, false, "reasoning_cache_invalid") => true,
                (503, false, "reasoning_receipt_unavailable") => true,
                _ => false,
            };
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParseAction(
        JsonElement action,
        IReadOnlyList<string> allowedActions,
        out RuleActionSpec? actionSpec)
    {
        actionSpec = null;
        if (!HasExactKeys(action, "type", "parameters") ||
            !TryString(action, "type", out var actionName) ||
            !allowedActions.Contains(actionName, StringComparer.Ordinal) ||
            !ActionTypes.TryGetValue(actionName, out var actionType) ||
            !action.TryGetProperty("parameters", out var parameters) ||
            parameters.ValueKind != JsonValueKind.Object)
            return false;
        var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in parameters.EnumerateObject())
        {
            if (parsed.Count >= 16 || !ParameterKeyPattern.IsMatch(property.Name) ||
                property.Value.ValueKind != JsonValueKind.String)
                return false;
            var value = property.Value.GetString();
            if (value is null || value.Length > 200 || !IsPhiNegative(value) ||
                !parsed.TryAdd(property.Name, value))
                return false;
        }
        if (!InferenceActionParameterContract.IsExact(actionType, parsed))
            return false;
        actionSpec = new RuleActionSpec
        {
            Type = actionType,
            Parameters = parsed,
        };
        return true;
    }

    private static string[] NormalizeAllowedActions(
        IReadOnlySet<RuleActionType> allowedActions)
    {
        ArgumentNullException.ThrowIfNull(allowedActions);
        if (allowedActions.Count is < 1 or > 8)
            throw new InvalidDataException("reasoning_actions_invalid");
        var values = allowedActions
            .Select(value => Enum.GetName(value))
            .ToArray();
        if (values.Any(value => value is null) ||
            values.Distinct(StringComparer.Ordinal).Count() != values.Length)
            throw new InvalidDataException("reasoning_actions_invalid");
        return values!
            .Select(value => value!)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string StructuralFingerprintToken(
        StructuralElementObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var signature = observation.Signature;
        ArgumentNullException.ThrowIfNull(signature);
        var automationId = string.IsNullOrEmpty(signature.AutomationId)
            ? null
            : signature.AutomationId;
        var className = string.IsNullOrEmpty(signature.ClassName)
            ? null
            : signature.ClassName;
        if (!StructuralRoles.Contains(signature.ControlType) ||
            automationId is not null &&
                !StructuralComponentPattern.IsMatch(automationId) ||
            className is not null &&
                !StructuralComponentPattern.IsMatch(className) ||
            !IsPhiNegative(signature.ControlType) ||
            automationId is not null && !IsPhiNegative(automationId) ||
            className is not null && !IsPhiNegative(className))
            throw new InvalidDataException("reasoning_state_invalid");

        var identityJson = JsonSerializer.Serialize(new StructuralIdentity(
            automationId,
            className,
            signature.ControlType));
        var canonical = Rfc8785Canonicalizer.CanonicalizeToUtf8(identityJson);
        try
        {
            var digest = SHA256.HashData(canonical);
            return signature.ControlType + ":" +
                Convert.ToHexString(digest).ToLowerInvariant() + ":" +
                observation.StateByte.ToString("x2", CultureInfo.InvariantCulture);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonical);
        }
    }

    private static bool HasValidVerifiedEnvelope(VerifiedCloudPostResponse response)
    {
        if (response.StatusCode is < 100 or > 599 ||
            response.Body.Length == 0 ||
            Encoding.UTF8.GetByteCount(response.Body) > MaximumResponseBytes ||
            response.KeyId != RemoteCommandTrust.CommandV1KeyId ||
            !IsLowerHexSha256(response.BodySha256))
            return false;
        try
        {
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(response.Body));
            try
            {
                return CryptographicOperations.FixedTimeEquals(
                        digest, Convert.FromHexString(response.BodySha256)) &&
                    Convert.FromBase64String(response.SignatureBase64).Length == 64;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(digest);
            }
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool HasExactKeys(JsonElement element, params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object) return false;
        var names = element.EnumerateObject().Select(property => property.Name).ToArray();
        return names.Length == expected.Length &&
            names.Distinct(StringComparer.Ordinal).Count() == names.Length &&
            names.ToHashSet(StringComparer.Ordinal).SetEquals(expected);
    }

    private static bool TryString(
        JsonElement root,
        string property,
        out string value)
    {
        value = "";
        if (!root.TryGetProperty(property, out var element) ||
            element.ValueKind != JsonValueKind.String)
            return false;
        value = element.GetString() ?? "";
        return true;
    }

    private static bool TryInt(JsonElement root, string property, out int value)
    {
        value = 0;
        return root.TryGetProperty(property, out var element) &&
            element.ValueKind == JsonValueKind.Number &&
            element.TryGetInt32(out value);
    }

    private static bool TryFiniteDouble(
        JsonElement root,
        string property,
        out double value)
    {
        value = 0;
        return root.TryGetProperty(property, out var element) &&
            element.ValueKind == JsonValueKind.Number &&
            element.TryGetDouble(out value) &&
            double.IsFinite(value);
    }

    private static bool TryBoundUuid(
        JsonElement root,
        string property,
        out string value)
    {
        value = "";
        if (!TryString(root, property, out var text) ||
            !Guid.TryParseExact(text, "D", out var parsed) ||
            !IsUuidV4(parsed) || text != parsed.ToString("D"))
            return false;
        value = text;
        return true;
    }

    private static bool IsPhiNegative(string value) =>
        !CorePhiScrubber.ContainsPhi(value) &&
        string.Equals(
            CorePhiScrubber.ScrubText(value), value, StringComparison.Ordinal);

    private static string NormalizeUuidV4(string? value, string field)
    {
        if (!Guid.TryParseExact(value, "D", out var parsed) || !IsUuidV4(parsed))
            throw new InvalidDataException($"reasoning_{field}_identity_invalid");
        return parsed.ToString("D");
    }

    private static bool IsUuidV4(Guid value)
    {
        var text = value.ToString("D");
        return text[14] == '4' && text[19] is '8' or '9' or 'a' or 'b';
    }

    private static bool IsLowerHexSha256(string value) =>
        value.Length == 64 &&
        value.All(character =>
            character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static FrozenSet<string> Set(params string[] values) =>
        values.ToFrozenSet(StringComparer.Ordinal);

    internal sealed record ReasonRequest(
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("requestId")] string RequestId,
        [property: JsonPropertyName("skillId")] string SkillId,
        [property: JsonPropertyName("scrubbedState")] ReasonScrubbedState ScrubbedState,
        [property: JsonPropertyName("escalationCode")] string EscalationCode,
        [property: JsonPropertyName("allowedActions")] string[] AllowedActions);

    internal sealed record ReasonScrubbedState(
        [property: JsonPropertyName("processName")] string ProcessName,
        [property: JsonPropertyName("windowTitle")] string WindowTitle,
        [property: JsonPropertyName("visibleElements")] string[] VisibleElements,
        [property: JsonPropertyName("operatorIdleMs")] int OperatorIdleMs,
        [property: JsonPropertyName("flags")]
            IReadOnlyDictionary<string, string> Flags,
        [property: JsonPropertyName("userObjective")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            string? UserObjective);

    private sealed record ReasonStateHashInput(
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("skillId")] string SkillId,
        [property: JsonPropertyName("escalationCode")] string EscalationCode,
        [property: JsonPropertyName("allowedActions")] string[] AllowedActions,
        [property: JsonPropertyName("scrubbedState")] ReasonScrubbedState ScrubbedState);

    private sealed record StructuralIdentity(
        [property: JsonPropertyName("automationId")] string? AutomationId,
        [property: JsonPropertyName("className")] string? ClassName,
        [property: JsonPropertyName("controlType")] string ControlType);
}
