using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SuavoAgent.Diagnostics;

/// <summary>
/// Fail-closed RFC 8785 boundary over the pinned, vendored WebPKI reference
/// implementation. Invalid JSON, duplicate names, non-finite numbers, and
/// invalid Unicode never produce signable bytes or leak input through errors.
/// </summary>
public static class Rfc8785Canonicalizer
{
    public const int MaximumInputBytes = 1024 * 1024;
    public const int MaximumDepth = 64;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static string Canonicalize(string json) =>
        StrictUtf8.GetString(CanonicalizeToUtf8(json));

    public static byte[] CanonicalizeToUtf8(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        byte[] utf8 = [];
        try
        {
            if (json.Length > MaximumInputBytes)
                throw new InvalidDataException();

            utf8 = StrictUtf8.GetBytes(json);
            if (utf8.Length > MaximumInputBytes)
                throw new InvalidDataException();

            using var document = JsonDocument.Parse(
                utf8,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = MaximumDepth,
                });
            if (document.RootElement.ValueKind is not (
                    JsonValueKind.Object or JsonValueKind.Array))
                throw new InvalidDataException();
            ValidateElement(document.RootElement);

            return new Org.Webpki.JsonCanonicalizer.JsonCanonicalizer(utf8)
                .GetEncodedUTF8();
        }
        catch (Exception exception) when (exception is
            IOException or
            InvalidDataException or
            ArgumentException or
            FormatException or
            OverflowException or
            InvalidOperationException or
            JsonException)
        {
            // Do not attach the parser exception: duplicate property names and
            // malformed tokens may contain sensitive source text.
            throw new JsonException("rfc8785_input_invalid");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(utf8);
        }
    }

    private static void ValidateElement(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    ValidateUnicode(property.Name);
                    if (!names.Add(property.Name))
                        throw new InvalidDataException();
                    ValidateElement(property.Value);
                }
                break;
            }
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    ValidateElement(item);
                break;
            case JsonValueKind.String:
                ValidateUnicode(element.GetString() ?? string.Empty);
                break;
            case JsonValueKind.Number:
                if (!element.TryGetDouble(out var number) || !double.IsFinite(number))
                    throw new InvalidDataException();
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                break;
            default:
                throw new InvalidDataException();
        }
    }

    private static void ValidateUnicode(string value)
    {
        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            var status = Rune.DecodeFromUtf16(remaining, out _, out var consumed);
            if (status != OperationStatus.Done || consumed <= 0)
                throw new InvalidDataException();
            remaining = remaining[consumed..];
        }
    }
}
