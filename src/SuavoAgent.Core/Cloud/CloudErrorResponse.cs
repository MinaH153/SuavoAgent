using System.Net;

namespace SuavoAgent.Core.Cloud;

/// <summary>
/// Attaches a bounded response body to an ordinary HttpRequestException for
/// exact protocol parsing. The body is private transport evidence and must
/// never be logged.
/// </summary>
internal static class CloudErrorResponse
{
    private const string ResponseBodyKey = "suavo.cloud.response_body";

    internal static HttpRequestException Create(
        string message,
        HttpStatusCode statusCode,
        string? responseBody)
    {
        var exception = new HttpRequestException(message, null, statusCode);
        if (responseBody is not null)
            exception.Data[ResponseBodyKey] = responseBody;
        return exception;
    }

    internal static string? ReadBody(HttpRequestException exception) =>
        exception.Data[ResponseBodyKey] as string;
}
