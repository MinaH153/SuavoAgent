using System.Threading;
using System.Threading.Tasks;
using SuavoAgent.Contracts.Learning;

namespace SuavoAgent.Core.Cloud;

public enum SchemaAdaptationUploadOutcome
{
    Uploaded,
    AlreadyStored,
    Rejected,
    TransportFailed,
}

public sealed record SchemaAdaptationUploadResult(
    SchemaAdaptationUploadOutcome Outcome,
    string? Detail);

/// <summary>
/// Posts a signed <see cref="SchemaAdaptation"/> to the cloud upload endpoint
/// (<c>POST /api/agent/schema-adaptation</c>). Orchestration (when to build,
/// when to retry) is owned by the caller. Implementations MUST NOT throw —
/// they must return a result with <see cref="SchemaAdaptationUploadOutcome.TransportFailed"/>
/// on network or auth error so the caller can decide retry policy.
/// </summary>
public interface ISchemaAdaptationUploader
{
    Task<SchemaAdaptationUploadResult> UploadAsync(SchemaAdaptation adaptation, CancellationToken ct);
}
