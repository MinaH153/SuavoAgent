using System;

namespace SuavoAgent.Contracts.Annotations;

/// <summary>
/// Marks a record / class / struct / interface as a network-bound payload type.
/// The Roslyn analyzer in <c>SuavoAgent.Analyzers</c> walks the type's member
/// graph (with cycle guard, capped at 50 levels) and fails the build if any
/// nested member carries <see cref="PhiDirectAttribute"/>.
///
/// Apply this to types that cross the agent ↔ cloud boundary:
/// HMAC sync payloads, heartbeat events, audit event payloads, signed verb
/// invocations, model prompts. See <c>docs/self-healing/field-registry.md</c>
/// for the canonical PHI tier classification.
/// </summary>
[AttributeUsage(
    AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface,
    AllowMultiple = false,
    Inherited = true)]
public sealed class OutboundPayloadAttribute : Attribute;
