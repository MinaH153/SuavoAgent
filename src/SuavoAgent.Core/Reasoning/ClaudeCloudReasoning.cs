using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SuavoAgent.Contracts.Reasoning;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Learning;

namespace SuavoAgent.Core.Reasoning;

/// <summary>
/// Tier-3 cloud reasoning backed by Anthropic's Claude (via the Suavo cloud
/// endpoint at <c>/api/agent/reason</c>). The agent never talks to Anthropic
/// directly — all calls go through our HMAC-signed cloud route, which:
///
///   - Re-scrubs the state defensively
///   - Caches by SHA-256 of the scrubbed input (30d TTL)
///   - Rate-limits per agent (50/day)
///   - Logs every call to <c>agent_reasoning_log</c> for audit
///
/// PHI safety: we pre-scrub string fields (WindowTitle, VisibleElements,
/// Flags values, EscalationReason) via <see cref="PhiScrubber.ScrubText"/>
/// before they leave the agent. Defense-in-depth with the cloud-side scrub.
/// </summary>
public sealed class ClaudeCloudReasoning : ICloudReasoning
{
    private readonly IPostSigner _cloud;
    private readonly ILogger<ClaudeCloudReasoning> _logger;
    private const string Endpoint = "/api/agent/reason";

    public ClaudeCloudReasoning(IPostSigner cloud, ILogger<ClaudeCloudReasoning> logger)
    {
        _cloud = cloud;
        _logger = logger;
    }

    public bool IsEnabled => true;

    public async Task<InferenceProposal?> ProposeAsync(
        InferenceRequest request,
        string tier2EscalationReason,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var payload = BuildScrubbedPayload(request, tier2EscalationReason);
            var response = await _cloud.PostSignedAsync(Endpoint, payload, ct);
            if (response is null)
            {
                _logger.LogWarning("Tier-3: cloud returned null body");
                return null;
            }

            return ParseProposal(response.Value, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Fail-closed: Tier-3 errors escalate to the operator, never surface.
            _logger.LogWarning(ex, "Tier-3: cloud call failed — escalating to operator");
            return null;
        }
    }

    /// <summary>
    /// Builds the request payload. Strings are scrubbed via PhiScrubber before
    /// leaving the process — even though the cloud re-scrubs, belt-and-braces.
    /// </summary>
    internal static object BuildScrubbedPayload(
        InferenceRequest request,
        string tier2EscalationReason)
    {
        var ctx = request.Context;

        var scrubbedState = new Dictionary<string, object?>
        {
            ["processName"] = ctx.ProcessName,  // process names are not PHI
            ["windowTitle"] = PhiScrubber.ScrubText(ctx.WindowTitle) ?? "",
            ["visibleElements"] = ctx.VisibleElements
                .Select(e => PhiScrubber.ScrubText(e) ?? "")
                .Where(e => !string.IsNullOrEmpty(e))
                .ToArray(),
            ["operatorIdleMs"] = ctx.OperatorIdleMs,
            ["flags"] = ctx.Flags.ToDictionary(
                kvp => kvp.Key,
                kvp => (object?)(PhiScrubber.ScrubText(kvp.Value) ?? "")),
        };

        // AllowedActions is part of the cloud contract — the cloud enforces that
        // the model's output uses one of these exact strings.
        var allowedActions = request.AllowedActions
            .Select(a => a.ToString())
            .ToArray();

        return new
        {
            skillId = ctx.SkillId,
            scrubbedState,
            escalationReason = PhiScrubber.ScrubText(tier2EscalationReason) ?? "",
            allowedActions,
        };
    }

    /// <summary>
    /// Parses the cloud response into an InferenceProposal. Expects the
    /// envelope: <c>{ success, data: { action: {...}, confidence, rationale,
    /// modelId, cached, latencyMs } }</c>. Returns null on any shape mismatch.
    /// </summary>
    internal static InferenceProposal? ParseProposal(JsonElement root, long elapsedMs)
    {
        if (!root.TryGetProperty("success", out var okEl) || !okEl.GetBoolean()) return null;
        if (!root.TryGetProperty("data", out var data)) return null;

        if (!data.TryGetProperty("action", out var action)) return null;
        if (!action.TryGetProperty("type", out var typeEl)) return null;
        if (!Enum.TryParse<RuleActionType>(typeEl.GetString(), ignoreCase: true, out var actType))
            return null;

        var parameters = new Dictionary<string, string>();
        if (action.TryGetProperty("parameters", out var paramsEl)
            && paramsEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in paramsEl.EnumerateObject())
            {
                parameters[p.Name] = p.Value.ValueKind switch
                {
                    JsonValueKind.String => p.Value.GetString() ?? "",
                    JsonValueKind.Number => p.Value.ToString(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => p.Value.GetRawText(),
                };
            }
        }

        double confidence = 0.5;
        if (data.TryGetProperty("confidence", out var confEl)
            && confEl.ValueKind == JsonValueKind.Number)
        {
            confidence = Math.Clamp(confEl.GetDouble(), 0.0, 1.0);
        }

        string? rationale = null;
        if (data.TryGetProperty("rationale", out var ratEl)
            && ratEl.ValueKind == JsonValueKind.String)
        {
            rationale = ratEl.GetString();
        }

        var modelId = data.TryGetProperty("modelId", out var midEl)
            && midEl.ValueKind == JsonValueKind.String
            ? midEl.GetString() ?? "cloud-unknown"
            : "cloud-unknown";

        return new InferenceProposal
        {
            Action = new RuleActionSpec { Type = actType, Parameters = parameters },
            Confidence = confidence,
            ModelId = modelId,
            Rationale = rationale,
            LatencyMs = elapsedMs,
        };
    }
}
