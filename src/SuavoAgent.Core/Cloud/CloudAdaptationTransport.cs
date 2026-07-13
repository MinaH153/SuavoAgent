using System;
using System.Threading;
using System.Threading.Tasks;

namespace SuavoAgent.Core.Cloud;

/// <summary>
/// Direct peer-authored schema adaptation fanout is retired. The production
/// fleet-learning lane is the human-reviewed, cloud-signed seed channel.
/// Keeping this transport fail-explicit prevents a future feature-flag change
/// from mistaking an unavailable authority for an empty update set.
/// </summary>
public sealed class CloudAdaptationTransport : ISchemaAdaptationTransport
{
    public CloudAdaptationTransport(IPostSigner client, string publicKeyDer)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKeyDer);
    }

    public Task<AdaptationPullResponse?> PullAsync(
        string pmsType,
        string fromSchemaHash,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        throw new InvalidOperationException("schema_adaptation_distribution_retired");
    }
}
