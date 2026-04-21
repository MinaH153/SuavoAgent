namespace SuavoAgent.Contracts.Discovery;

/// <summary>
/// Structural fingerprint of a candidate file's contents. <see cref="Shape"/>
/// is polymorphic so each sampler (Tabular/Document/Email/…) returns its
/// own type without the locator knowing specifics.
/// </summary>
public sealed record FileCandidateSample(
    FileCandidate Candidate,
    ShapeSample? Shape,
    string? ErrorMessage = null);
