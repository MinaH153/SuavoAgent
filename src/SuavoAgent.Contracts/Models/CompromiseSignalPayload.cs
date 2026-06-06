using System;
using SuavoAgent.Contracts.Annotations;

namespace SuavoAgent.Contracts.Models;

/// <summary>
/// The self-compromise signal the agent emits on the heartbeat when the honeytoken immune reflex fires.
/// Rides the existing signed heartbeat (no new endpoint/auth); the cloud uses it to fleet-revoke a
/// suspected-compromised ("cancer cell") agent.
///
/// PHI-FREE BY CONSTRUCTION: <c>[OutboundPayload]</c> makes the Roslyn analyzer fail the build if any
/// member ever carries <c>[PhiDirect]</c>. This record NEVER carries the honeytoken file PATH or CONTENTS —
/// only <see cref="HoneytokenId"/> (a SHA-256 of the token NAME, opaque), the corroboration level, and a
/// sanitized ≤32-char reason label (the offending process name reduced to a safe charset by
/// <c>HoneytokenCorroborator</c>). Mirrors <see cref="HealthCompositePayload"/>.
///
/// <see cref="CorroborationLevel"/> values: <c>"observe"</c> | <c>"degrade"</c> | <c>"apoptosis"</c>.
/// Only <c>"apoptosis"</c> drives a cloud fleet-revoke; lower rungs are alarm + audit only.
/// </summary>
[OutboundPayload]
public sealed record CompromiseSignalPayload(
    bool Detected,
    bool HoneytokenTripped,
    string HoneytokenId,
    string CorroborationLevel,
    string ReasonLabel,
    DateTimeOffset OccurredAtUtc);
