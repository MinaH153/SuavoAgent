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
/// fixed <see cref="HoneytokenReasonLabels"/> category. Process names never
/// cross the Helper boundary. Mirrors <see cref="HealthCompositePayload"/>.
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

/// <summary>
/// Complete PHI-negative vocabulary for honeytoken audit/cloud reason labels.
/// Raw process identity is local forensic evidence and must never be embedded
/// in one of these values.
/// </summary>
public static class HoneytokenReasonLabels
{
    public const string AgentProcess = "agent_process";
    public const string SystemProcess = "system_process";
    public const string SensitiveShell = "sensitive_shell";
    public const string UnexpectedProcess = "unexpected_process";
    public const string UnknownProcess = "unknown_process";

    public static string Normalize(string? value) => value switch
    {
        AgentProcess => AgentProcess,
        SystemProcess => SystemProcess,
        SensitiveShell => SensitiveShell,
        UnexpectedProcess => UnexpectedProcess,
        UnknownProcess => UnknownProcess,
        _ => UnknownProcess,
    };
}
