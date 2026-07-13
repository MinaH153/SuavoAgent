using SuavoAgent.Contracts.Security;

namespace SuavoAgent.Core.Cloud;

public sealed class HmacSigner
{
    private readonly AgentRequestSigner _inner;

    public HmacSigner(string apiKey)
    {
        _inner = new AgentRequestSigner(apiKey);
    }

    public AgentRequestAuthorization ApplyHeaders(HttpRequestMessage request, string rawBody) =>
        _inner.ApplyHeaders(request, rawBody);

    public string Sign(
        string method,
        string pathAndQuery,
        string timestamp,
        string nonce,
        string bodySha256) =>
        _inner.Sign(method, pathAndQuery, timestamp, nonce, bodySha256);

    public static bool IsWithinReplayWindow(string timestamp, TimeSpan window)
    {
        return AgentRequestSigner.IsWithinReplayWindow(timestamp, window);
    }
}
