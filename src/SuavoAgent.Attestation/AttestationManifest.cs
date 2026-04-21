using System.Text.Json.Serialization;

namespace SuavoAgent.Attestation;

/// <summary>
/// Signed SBOM + file-hash manifest for a specific agent release. Produced
/// by the release pipeline, signed with the release signing key (ECDSA P-256),
/// published alongside release binaries. Agent fetches from
/// <c>/api/agent/attestation?version=X.Y.Z</c> on startup.
/// </summary>
/// <remarks>
/// v0.1 uses the same ECDSA P-256 key infrastructure already established for
/// OTA manifests. Sigstore cosign migration is deferred to v0.2 per
/// key-custody.md §Migration plan.
/// </remarks>
public sealed record AttestationManifest
{
    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("framework")]
    public required string Framework { get; init; }

    [JsonPropertyName("runtime")]
    public required string Runtime { get; init; }

    [JsonPropertyName("files")]
    public required IReadOnlyList<AttestedFile> Files { get; init; }

    [JsonPropertyName("manifest_version")]
    public required string ManifestVersion { get; init; } // semver of the manifest schema itself

    [JsonPropertyName("generated_at")]
    public required DateTimeOffset GeneratedAt { get; init; }
}

public sealed record AttestedFile(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("size_bytes")] long SizeBytes);
