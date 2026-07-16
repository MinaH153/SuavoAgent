using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SuavoAgent.Core.Cloud;

internal interface IRelease1ConvergenceTransport
{
    Task<bool> SendInstallReceiptAsync(
        string exactRequestJson,
        CancellationToken cancellationToken);

    Task<bool> AckChallengeAsync(
        string commandId,
        string exactRequestJson,
        CancellationToken cancellationToken);

    Task<string?> SendPreliminaryAsync(
        string exactRequestJson,
        CancellationToken cancellationToken);

    Task<bool> SendFinalAsync(
        string exactRequestJson,
        CancellationToken cancellationToken);
}

internal sealed class SuavoRelease1ConvergenceTransport(
    SuavoCloudClient cloudClient) : IRelease1ConvergenceTransport
{
    public Task<bool> SendInstallReceiptAsync(
        string exactRequestJson,
        CancellationToken cancellationToken) =>
        cloudClient.SendRelease1InstallReceiptAsync(
            exactRequestJson,
            cancellationToken);

    public Task<bool> AckChallengeAsync(
        string commandId,
        string exactRequestJson,
        CancellationToken cancellationToken) =>
        cloudClient.AckRelease1ChallengeAsync(
            commandId,
            exactRequestJson,
            cancellationToken);

    public Task<string?> SendPreliminaryAsync(
        string exactRequestJson,
        CancellationToken cancellationToken) =>
        cloudClient.SendRelease1PreliminaryAsync(
            exactRequestJson,
            cancellationToken);

    public Task<bool> SendFinalAsync(
        string exactRequestJson,
        CancellationToken cancellationToken) =>
        cloudClient.SendRelease1FinalAsync(
            exactRequestJson,
            cancellationToken);
}

public sealed partial class SuavoCloudClient
{
    private const int MaxRelease1ResponseBytes = 16 * 1024;

    internal async Task<bool> SendRelease1InstallReceiptAsync(
        string exactRequestJson,
        CancellationToken cancellationToken)
    {
        var response = await PostRelease1ExactAsync(
                "/api/agent/release1/install-receipt",
                exactRequestJson,
                allowEmptyResponse: false,
                cancellationToken)
            .ConfigureAwait(false);
        RequireRelease1SuccessResponse(response, "install receipt");
        return true;
    }

    internal async Task<bool> AckRelease1ChallengeAsync(
        string commandId,
        string exactRequestJson,
        CancellationToken cancellationToken)
    {
        if (!IsCanonicalUuid(commandId))
            throw new ArgumentException(
                "Release 1 command id is invalid.",
                nameof(commandId));
        _ = await PostRelease1ExactAsync(
                $"/api/agent/commands/{commandId}/ack",
                exactRequestJson,
                allowEmptyResponse: true,
                cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    internal async Task<string?> SendRelease1PreliminaryAsync(
        string exactRequestJson,
        CancellationToken cancellationToken)
    {
        var response = await PostRelease1ExactAsync(
                "/api/agent/release1/preliminary",
                exactRequestJson,
                allowEmptyResponse: false,
                cancellationToken)
            .ConfigureAwait(false);
        if (response is null ||
            response.Value.ValueKind != JsonValueKind.Object ||
            !HasExactProperties(response.Value, "success", "data") ||
            !response.Value.TryGetProperty("success", out var success) ||
            success.ValueKind != JsonValueKind.True ||
            !response.Value.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object ||
            !HasExactProperties(data, "commandId") ||
            !data.TryGetProperty("commandId", out var commandIdElement) ||
            commandIdElement.ValueKind != JsonValueKind.String ||
            commandIdElement.GetString() is not { } commandId ||
            !IsCanonicalUuid(commandId))
            throw new InvalidDataException(
                "Release 1 preliminary response is invalid.");
        return commandId;
    }

    internal async Task<bool> SendRelease1FinalAsync(
        string exactRequestJson,
        CancellationToken cancellationToken)
    {
        var response = await PostRelease1ExactAsync(
                "/api/agent/release1/convergence",
                exactRequestJson,
                allowEmptyResponse: false,
                cancellationToken)
            .ConfigureAwait(false);
        RequireRelease1SuccessResponse(response, "final");
        return true;
    }

    private static void RequireRelease1SuccessResponse(
        JsonElement? response,
        string label)
    {
        if (response is null ||
            response.Value.ValueKind != JsonValueKind.Object ||
            !HasExactProperties(response.Value, "success") ||
            !response.Value.TryGetProperty("success", out var success) ||
            success.ValueKind != JsonValueKind.True)
            throw new InvalidDataException(
                $"Release 1 {label} response is invalid.");
    }

    private async Task<JsonElement?> PostRelease1ExactAsync(
        string path,
        string exactRequestJson,
        bool allowEmptyResponse,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exactRequestJson);
        OutboundPhiGuard.AssertAllowed(path, exactRequestJson, _options);
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(
                exactRequestJson,
                Encoding.UTF8,
                "application/json"),
        };
        _signer.ApplyHeaders(request, exactRequestJson);
        using var response = await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException(
                "Release 1 convergence endpoint rejected the request.",
                null,
                response.StatusCode);
        if (response.Content is null || response.Content.Headers.ContentLength == 0)
        {
            if (allowEmptyResponse) return null;
            throw new InvalidDataException("Release 1 response body is missing.");
        }
        var bytes = await ReadResponseBytesBoundedAsync(
                response,
                MaxRelease1ResponseBytes,
                cancellationToken)
            .ConfigureAwait(false);
        if (bytes is null)
        {
            if (allowEmptyResponse) return null;
            throw new InvalidDataException("Release 1 response exceeds its bound.");
        }
        try
        {
            return JsonSerializer.Deserialize<JsonElement>(bytes);
        }
        catch (JsonException exception)
        {
            if (allowEmptyResponse) return null;
            throw new InvalidDataException("Release 1 response JSON is invalid.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
