using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SuavoAgent.Contracts.Learning;

namespace SuavoAgent.Core.Cloud;

/// <summary>
/// HTTP implementation of <see cref="ISchemaAdaptationTransport"/>. Posts to
/// <c>/api/agent/adaptations/pull</c> via <see cref="SuavoCloudClient.PostSignedVerifiedAsync"/>
/// so the overall response envelope is ECDSA-verified before deserialization
/// (H-11 pattern — same as seeds).
///
/// v3.12.1 ships this with the server endpoint stubbed. Returns null on any
/// transport error so <see cref="Workers.SchemaAdaptationWorker"/> idles until
/// the next tick.
/// </summary>
public sealed class CloudAdaptationTransport : ISchemaAdaptationTransport
{
    private readonly SuavoCloudClient _client;
    private readonly string _publicKeyDer;

    public CloudAdaptationTransport(SuavoCloudClient client, string publicKeyDer)
    {
        _client = client;
        _publicKeyDer = publicKeyDer;
    }

    public async Task<AdaptationPullResponse?> PullAsync(
        string pmsType, string fromSchemaHash, CancellationToken ct)
    {
        try
        {
            var payload = new { pmsType, fromSchemaHash };
            var raw = await _client.PostSignedVerifiedAsync(
                "/api/agent/adaptations/pull", payload, _publicKeyDer, ct);
            if (raw is not JsonElement el) return null;
            return JsonSerializer.Deserialize<AdaptationPullResponse>(el.GetRawText());
        }
        catch
        {
            return null;
        }
    }
}
