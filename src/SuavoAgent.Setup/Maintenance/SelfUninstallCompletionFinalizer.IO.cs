using System.Text;
using SuavoAgent.Contracts.Maintenance;

namespace SuavoAgent.Setup.Maintenance;

internal static partial class SelfUninstallCompletionFinalizer
{
    internal static HttpClientHandler CreateFinalizationHandler() => new()
    {
        AllowAutoRedirect = false,
    };

    private static HttpClient CreateHttpClient() => new(CreateFinalizationHandler())
    {
        Timeout = RequestTimeout,
    };

    private static async Task<SelfUninstallFinalizePostResult> PostAsync(
        HttpClient http,
        Uri origin,
        string exactBody,
        CancellationToken cancellationToken)
    {
        if (origin.Scheme != Uri.UriSchemeHttps ||
            !origin.IsDefaultPort ||
            !string.Equals(origin.Host, "suavollc.com", StringComparison.OrdinalIgnoreCase) ||
            origin.AbsolutePath is not ("" or "/") ||
            !string.IsNullOrEmpty(origin.Query) ||
            !string.IsNullOrEmpty(origin.Fragment) ||
            !string.IsNullOrEmpty(origin.UserInfo))
            throw new InvalidOperationException(
                "Self-uninstall completion origin is not the pinned production origin.");
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(origin, SelfUninstallCompletionContract.FinalizeEndpoint))
        {
            Content = new StringContent(exactBody, Encoding.UTF8, "application/json"),
        };
        using var response = await http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        var body = await ReadBoundedAsync(
            response.Content,
            SelfUninstallCompletionContract.MaxResponseBytes,
            cancellationToken).ConfigureAwait(false);
        return new(response.IsSuccessStatusCode, body);
    }

    internal static async Task<SelfUninstallFinalizePostResult> PostForTestsAsync(
        HttpMessageHandler handler,
        Uri origin,
        string exactBody,
        CancellationToken cancellationToken)
    {
        using var http = new HttpClient(handler) { Timeout = RequestTimeout };
        return await PostAsync(http, origin, exactBody, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<string> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > 0 &&
            content.Headers.ContentLength > maximumBytes)
            throw new InvalidDataException("Finalize response exceeds the allowed size.");
        await using var input = await content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (memory.Length + read > maximumBytes)
                throw new InvalidDataException("Finalize response exceeds the allowed size.");
            memory.Write(buffer, 0, read);
        }
        return new UTF8Encoding(false, true).GetString(memory.ToArray());
    }

    private static string ReadBoundedFile(string path, int maximumBytes)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.SequentialScan);
        if (stream.Length is <= 0 || stream.Length > maximumBytes)
            throw new InvalidDataException("Protected maintenance file has an invalid size.");
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096,
            leaveOpen: true);
        var value = reader.ReadToEnd();
        if (stream.Position != stream.Length)
            throw new InvalidDataException("Protected maintenance file was not read exactly.");
        return value;
    }
}
