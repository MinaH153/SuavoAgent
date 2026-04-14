using System.Text.Json;
using SuavoAgent.Core.Learning;

namespace SuavoAgent.Core.Cloud;

public sealed class SeedClient
{
    private readonly IPostSigner _signer;

    public SeedClient(IPostSigner signer) => _signer = signer;

    public async Task<SeedResponse?> PullAsync(SeedRequest request, CancellationToken ct)
    {
        var result = await _signer.PostSignedAsync("/api/agent/seed/pull", request, ct);
        if (result is null) return null;
        return JsonSerializer.Deserialize<SeedResponse>(result.Value.GetRawText());
    }

    public async Task ConfirmAsync(SeedConfirmRequest request, CancellationToken ct)
    {
        await _signer.PostSignedAsync("/api/agent/seed/confirm", request, ct);
    }
}
