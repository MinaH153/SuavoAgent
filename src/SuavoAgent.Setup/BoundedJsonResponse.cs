using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SuavoAgent.Setup;

internal static class BoundedJsonResponse
{
    private const int MaxNodes = 4_096;
    private static readonly UTF8Encoding FatalUtf8 = new(false, true);
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 32,
    };

    internal static async Task<JsonObject> ReadObjectAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (maximumBytes <= 0 ||
            content.Headers.ContentLength is long declared &&
            (declared <= 0 || declared > maximumBytes))
            throw new InvalidDataException("Cloud response exceeds its allowed size.");
        await using var input = await content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[8 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (memory.Length > maximumBytes - read)
                throw new InvalidDataException("Cloud response exceeds its allowed size.");
            memory.Write(buffer, 0, read);
        }
        if (memory.Length == 0)
            throw new InvalidDataException("Cloud response is empty.");
        var json = FatalUtf8.GetString(memory.GetBuffer(), 0, checked((int)memory.Length));
        using var document = JsonDocument.Parse(json, DocumentOptions);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Cloud response must be one JSON object.");
        var nodes = 0;
        ValidateStrict(document.RootElement, ref nodes);
        return JsonNode.Parse(
                   json,
                   new JsonNodeOptions { PropertyNameCaseInsensitive = false },
                   DocumentOptions)?.AsObject()
               ?? throw new InvalidDataException("Cloud response is empty.");
    }

    private static void ValidateStrict(JsonElement element, ref int nodes)
    {
        if (++nodes > MaxNodes)
            throw new InvalidDataException("Cloud response is too complex.");
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidDataException(
                        "Cloud response contains a duplicate JSON property.");
                ValidateStrict(property.Value, ref nodes);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                ValidateStrict(item, ref nodes);
        }
    }
}
