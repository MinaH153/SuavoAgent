namespace SuavoAgent.Verbs;

/// <summary>
/// Typed input contract for a verb. Cloud dispatcher validates invocations
/// against this schema BEFORE the agent sees them. Agent re-validates at
/// dispatch time for defense in depth.
/// </summary>
public sealed record VerbParameterSchema(IReadOnlyList<VerbParameterDefinition> Required)
{
    public static VerbParameterSchema Empty { get; } = new(Array.Empty<VerbParameterDefinition>());
}

public sealed record VerbParameterDefinition(
    string Name,
    Type ClrType,
    /// <summary>Optional validation hint: "enum:A|B|C", "regex:^...$", "range:0..100", etc.</summary>
    string? ValidationHint = null);

/// <summary>
/// Typed output contract. Declared for auditability — cloud audit chain
/// records output_hash (hash of the serialized output dict matching this
/// schema) per verb.executed event.
/// </summary>
public sealed record VerbOutputSchema(IReadOnlyList<VerbOutputField> Fields)
{
    public static VerbOutputSchema Empty { get; } = new(Array.Empty<VerbOutputField>());
}

public sealed record VerbOutputField(string Name, Type ClrType);
