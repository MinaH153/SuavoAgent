using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SuavoAgent.Contracts.Learning;

namespace SuavoAgent.Core.Cloud;

/// <summary>
/// HTTP implementation of <see cref="ISchemaAdaptationUploader"/>. Posts via
/// <see cref="SuavoCloudClient.PostSignedAsync"/> so agent-to-cloud HMAC auth
/// is handled uniformly with the rest of the agent API surface.
///
/// Idempotency: the cloud endpoint returns <c>alreadyStored:true</c> on the
/// same <c>adaptationId</c>, which we surface as
/// <see cref="SchemaAdaptationUploadOutcome.AlreadyStored"/>. Callers should
/// treat that as success.
/// </summary>
public sealed class CloudSchemaAdaptationUploader : ISchemaAdaptationUploader
{
    private readonly IPostSigner _client;

    public CloudSchemaAdaptationUploader(IPostSigner client)
    {
        _client = client;
    }

    public async Task<SchemaAdaptationUploadResult> UploadAsync(
        SchemaAdaptation adaptation, CancellationToken ct)
    {
        JsonElement? response;
        try
        {
            response = await _client.PostSignedAsync(
                "/api/agent/schema-adaptation", adaptation, ct);
        }
        catch (System.Net.Http.HttpRequestException ex)
        {
            return new(SchemaAdaptationUploadOutcome.TransportFailed, ex.Message);
        }
        catch (System.Threading.Tasks.TaskCanceledException ex)
        {
            return new(SchemaAdaptationUploadOutcome.TransportFailed, ex.Message);
        }

        if (response is not JsonElement el)
            return new(SchemaAdaptationUploadOutcome.TransportFailed, "no response body");

        // Endpoint shape: { success: bool, alreadyStored?: bool, error?: string }
        var success = el.TryGetProperty("success", out var succEl) && succEl.GetBoolean();
        if (!success)
        {
            var err = el.TryGetProperty("error", out var errEl) ? errEl.GetString() : null;
            return new(SchemaAdaptationUploadOutcome.Rejected, err ?? "unspecified");
        }
        if (el.TryGetProperty("alreadyStored", out var alreadyEl) && alreadyEl.GetBoolean())
            return new(SchemaAdaptationUploadOutcome.AlreadyStored, null);

        return new(SchemaAdaptationUploadOutcome.Uploaded, null);
    }
}
