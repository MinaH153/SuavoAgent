using System;
using SuavoAgent.Contracts.Annotations;

namespace SuavoAgent.Contracts.Models;

/// <summary>
/// Audit payload for <c>agent.health_composite</c> event. Emitted by the
/// agent each heartbeat tick. Distinguishes "agent is sending heartbeats"
/// from "agent is actually healthy" via 4-component AND with off-hours
/// gating on <see cref="HealthCompositeComponents.ExtractionRecent"/>.
///
/// See <c>docs/superpowers/specs/2026-05-02-track-1-4-health-composite-design.md</c>.
///
/// Status values:
///   <list type="bullet">
///     <item><c>"healthy"</c> — all 4 components true</item>
///     <item><c>"heartbeating-but-unhealthy"</c> — heartbeat received but ≥1 component false</item>
///     <item><c>"initializing"</c> — agent install &lt; 2min old, no composite computed yet</item>
///   </list>
///
/// Note: <c>"silent"</c> is NOT an agent-side status — it is computed cloud-side
/// from heartbeat absence. Agent never emits <c>"silent"</c>.
/// </summary>
[OutboundPayload]
public sealed record HealthCompositePayload(
    string Status,
    HealthCompositeComponents Components,
    DateTimeOffset ComputedAt);
