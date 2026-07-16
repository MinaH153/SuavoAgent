using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Contracts.Vision;
using SuavoAgent.Core.Vision;

namespace SuavoAgent.Helper.Vision;

public sealed record VisionHandshakeResult(bool Accepted, string Code);

/// <summary>
/// Helper-side split-restart gate. The authenticated command pipe must prove
/// Core loaded the exact same registry generation and digest before any
/// pharmacy vision or vision-backed pricing request is admitted.
/// </summary>
public sealed class VisionGenerationGate
{
    private readonly long _localGeneration;
    private readonly string _localDigest;
    private int _matched;

    public VisionGenerationGate(VisionConfigurationLoadResult localState)
    {
        ArgumentNullException.ThrowIfNull(localState);
        if (!localState.IsValid)
            throw new ArgumentException("Local vision state must be valid.", nameof(localState));
        _localGeneration = localState.EffectiveGeneration;
        _localDigest = localState.State?.ConfigDigest ??
                       VisionConfigurationStateCodec.ComputeConfigDigest(
                           localState.EffectiveOptions);
    }

    public bool IsMatched => Volatile.Read(ref _matched) == 1;
    public long LocalGeneration => _localGeneration;
    public string LocalDigest => _localDigest;

    /// <summary>
    /// A successful proof belongs to one authenticated pipe connection. The
    /// single-instance Helper command server resets this latch when a new Core
    /// peer is authenticated, so an older connection can never authorize a
    /// later client that omitted the handshake.
    /// </summary>
    public void Reset() => Volatile.Write(ref _matched, 0);

    public VisionHandshakeResult VerifyAndLatch(JsonElement? data)
    {
        if (data is null || data.Value.ValueKind != JsonValueKind.Object)
            return Reject("vision_handshake_object_required");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in data.Value.EnumerateObject())
        {
            if (!names.Add(property.Name))
                return Reject("vision_handshake_duplicate_field");
        }
        if (!names.SetEquals(new[] { "schemaVersion", "generation", "configDigest" }))
            return Reject("vision_handshake_fields_invalid");
        if (!data.Value.TryGetProperty("schemaVersion", out var schema) ||
            !schema.TryGetInt32(out var schemaVersion) ||
            schemaVersion != VisionStateHandshake.CurrentSchemaVersion)
            return Reject("vision_handshake_schema_invalid");
        if (!data.Value.TryGetProperty("generation", out var generationElement) ||
            !generationElement.TryGetInt64(out var generation) || generation < 0)
            return Reject("vision_handshake_generation_invalid");
        if (!data.Value.TryGetProperty("configDigest", out var digestElement) ||
            digestElement.ValueKind != JsonValueKind.String ||
            digestElement.GetString() is not { } digest ||
            !VisionOptionsSnapshot.IsLowerHexSha256(digest))
            return Reject("vision_handshake_digest_invalid");

        var matches = generation == _localGeneration && FixedEquals(digest, _localDigest);
        Volatile.Write(ref _matched, matches ? 1 : 0);
        return matches
            ? new(true, "matched")
            : new(false, "vision_generation_mismatch");
    }

    private VisionHandshakeResult Reject(string code)
    {
        Volatile.Write(ref _matched, 0);
        return new(false, code);
    }

    private static bool FixedEquals(string left, string right) =>
        left.Length == right.Length && CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(left),
            Encoding.ASCII.GetBytes(right));
}
