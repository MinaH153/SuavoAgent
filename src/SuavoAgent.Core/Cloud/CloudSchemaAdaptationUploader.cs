using System;
using System.Threading;
using System.Threading.Tasks;
using SuavoAgent.Contracts.Learning;

namespace SuavoAgent.Core.Cloud;

/// <summary>
/// Peer-authored schema adaptation upload is retired with direct fanout.
/// A workstation must never populate a fleet corpus that the cloud cannot
/// independently verify. Scrubbed POM consensus and cloud-signed seeds remain
/// the supported fleet-learning path.
/// </summary>
public sealed class CloudSchemaAdaptationUploader : ISchemaAdaptationUploader
{
    public CloudSchemaAdaptationUploader(IPostSigner client) =>
        ArgumentNullException.ThrowIfNull(client);

    public Task<SchemaAdaptationUploadResult> UploadAsync(
        SchemaAdaptation adaptation,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(adaptation);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new SchemaAdaptationUploadResult(
            SchemaAdaptationUploadOutcome.Rejected,
            "schema_adaptation_upload_retired"));
    }
}
