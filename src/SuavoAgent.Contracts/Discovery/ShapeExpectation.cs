using System.Text.Json.Serialization;

namespace SuavoAgent.Contracts.Discovery;

/// <summary>
/// Polymorphic description of what shape of data the operator is looking
/// for. Concrete variants let each vertical describe its own priors
/// without the universal file-discovery foundation knowing specifics.
///
/// File discovery is intentionally file-type-agnostic — the enumerator
/// and heuristic scorer operate purely on (name, path, size, recency).
/// Only the content sampler dispatches by concrete shape.
///
/// Polymorphic JSON serialization uses a <c>$kind</c> discriminator so
/// these records can cross Core↔Helper IPC and cloud boundaries without
/// type information being lost.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
[JsonDerivedType(typeof(TabularExpectation), "tabular")]
[JsonDerivedType(typeof(DocumentExpectation), "document")]
[JsonDerivedType(typeof(EmailExpectation), "email")]
public abstract record ShapeExpectation;

/// <summary>
/// Tabular shape — spreadsheets, CSV/TSV exports, small database-export
/// files. First concrete variant; every other shape plugs in next to it.
/// </summary>
public sealed record TabularExpectation(
    IReadOnlyList<string>? ColumnHints = null,
    int? MinRows = null,
    int? MaxRows = null,
    ExpectedColumnPattern? PrimaryKeyPattern = null) : ShapeExpectation;

/// <summary>
/// Document-oriented content — PDFs, Word docs, policy manuals.
/// Not implemented in v3.13; reserved slot.
/// </summary>
public sealed record DocumentExpectation(
    IReadOnlyList<string>? HeadingHints = null,
    int? MinPages = null,
    int? MaxPages = null) : ShapeExpectation;

/// <summary>
/// Email message content — .msg/.eml/Outlook PST entries.
/// Not implemented in v3.13; reserved slot.
/// </summary>
public sealed record EmailExpectation(
    string? SubjectHint = null,
    string? SenderDomainHint = null,
    DateTimeOffset? AfterUtc = null) : ShapeExpectation;

/// <summary>
/// Regex shape for a "primary key"-like column — the column that uniquely
/// identifies each record. Vertical packs supply the regex (NDC format for
/// pharmacy, invoice-number format for accounting, case-number format for
/// legal, etc.). The sampler flags which column matches so the ranker
/// doesn't need to infer it.
/// </summary>
public sealed record ExpectedColumnPattern(
    string Name,
    string Regex,
    int MinSampleMatches = 3);
