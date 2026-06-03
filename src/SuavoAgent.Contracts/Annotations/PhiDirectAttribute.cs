using System;

namespace SuavoAgent.Contracts.Annotations;

/// <summary>
/// Marks a property or field as PHI-Direct per <c>docs/self-healing/field-registry.md</c>
/// classification. The Roslyn analyzer in <c>SuavoAgent.Analyzers</c> uses this to
/// fail any build where a field carrying this attribute appears on a type marked
/// <see cref="OutboundPayloadAttribute"/>.
///
/// PHI-Direct fields MUST NEVER cross the network in any form — events, verbs,
/// sync payloads, heartbeat, model prompts. They live exclusively in agent-local
/// SQLite, encrypted at rest. See meta-roadmap §9 invariant 1 for the
/// existential framing.
/// </summary>
[AttributeUsage(
    AttributeTargets.Property | AttributeTargets.Field,
    AllowMultiple = false,
    Inherited = true)]
public sealed class PhiDirectAttribute : Attribute;
