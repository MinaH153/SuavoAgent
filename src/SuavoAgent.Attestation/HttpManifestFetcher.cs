using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace SuavoAgent.Attestation;

public sealed class HttpManifestFetcher : IManifestFetcher
{
    private readonly HttpClient _http;
    private readonly ILogger<HttpManifestFetcher> _logger;
    private readonly string _cloudBaseUrl;
    private readonly string _pharmacyHeaderValue;

    public HttpManifestFetcher(
        HttpClient http,
        ILogger<HttpManifestFetcher> logger,
        string cloudBaseUrl,
        string pharmacyHeaderValue)
    {
        _http = http;
        _logger = logger;
        _cloudBaseUrl = cloudBaseUrl.TrimEnd('/');
        _pharmacyHeaderValue = pharmacyHeaderValue;
    }

    public async Task<SignedManifestPayload?> FetchAsync(string version, CancellationToken cancellationToken)
    {
        var url = $"{_cloudBaseUrl}/api/agent/attestation?version={Uri.EscapeDataString(version)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Pharmacy-Id", _pharmacyHeaderValue);
        request.Headers.Add("Accept", "application/json");

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "attestation manifest fetch returned {Status} for version {Version}",
                    (int)response.StatusCode, version);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            // Signature header contains hex-encoded ECDSA signature.
            if (!response.Headers.TryGetValues("X-Manifest-Signature", out var sigValues))
            {
                _logger.LogWarning("attestation manifest response missing X-Manifest-Signature header");
                return null;
            }
            var sigHex = sigValues.FirstOrDefault();
            if (string.IsNullOrEmpty(sigHex))
            {
                _logger.LogWarning("attestation manifest X-Manifest-Signature header is empty");
                return null;
            }

            var sigBytes = Convert.FromHexString(sigHex);
            return new SignedManifestPayload(json, sigBytes);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or SocketException)
        {
            _logger.LogWarning(ex, "network failure fetching attestation manifest for {Version}", version);
            return null;
        }
    }
}
