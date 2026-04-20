using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SuavoAgent.Contracts.Learning;

namespace SuavoAgent.Core.Cloud;

/// <summary>
/// Batch payload returned by the cloud adaptations endpoint. Both lists may be
/// empty; either may be null (older cloud versions). The overall response body
/// is ECDSA-verified by the transport before reaching the worker, so
/// <see cref="SchemaAdaptation"/> instances may still fail the per-record
/// signature check (defence in depth).
/// </summary>
public sealed record AdaptationPullResponse(
    [property: JsonPropertyName("adaptations")] IReadOnlyList<SchemaAdaptation>? Adaptations,
    [property: JsonPropertyName("revocations")] IReadOnlyList<AdaptationRevocation>? Revocations);

/// <summary>
/// Abstracts the wire protocol behind SchemaAdaptationWorker so tests can
/// inject a fake transport without an HTTP double. Implementations MUST
/// return null on transient failure (worker retries next tick) — never throw.
/// </summary>
public interface ISchemaAdaptationTransport
{
    Task<AdaptationPullResponse?> PullAsync(string pmsType, string fromSchemaHash,
        CancellationToken ct);
}
