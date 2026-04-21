using System.Text.Json.Serialization;

namespace SuavoAgent.Contracts.Discovery;

/// <summary>
/// Polymorphic content-sample output from a <see cref="ShapeExpectation"/>-
/// aware sampler. Carries structural signals (counts, indices, shape
/// booleans). Header/value content MAY carry PHI depending on the file's
/// subject domain; anything that will cross a process boundary or reach
/// an off-device LLM must be routed through the PHI scrubber first. Callers
/// must treat the ranker-facing projection (<c>FileCandidateForRanker</c>)
/// as the scrubbed/LLM-safe form; <c>ShapeSample</c> itself is raw.
///
/// JSON discriminator: <c>$kind</c>. Keep in sync when adding variants.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
[JsonDerivedType(typeof(TabularShapeSample), "tabular")]
public abstract record ShapeSample;

/// <summary>
/// Tabular sample — spreadsheets, CSVs, database tables.
/// <c>PrimaryKeyColumnIndex &gt;= 0</c> when a column matched the spec's
/// <see cref="ExpectedColumnPattern"/>.
/// </summary>
public sealed record TabularShapeSample(
    IReadOnlyList<string> ColumnHeaders,
    int RowCount,
    int PrimaryKeyColumnIndex,
    bool StructureMatchesHints) : ShapeSample
{
    public const int NoPrimaryKey = -1;
}
