using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SuavoAgent.Contracts.Security;

/// <summary>
/// PHI-free, Setup-owned local binding used by Core, Broker, Helper, and the
/// browser native host. The file is read-only to interactive users; a signed
/// lease must match every value exactly before it confers authority.
/// </summary>
public static partial class ObservationActivationIdentityStore
{
    public const int CurrentSchemaVersion = 1;
    public const string FileName = "observation-identity.json";
    public const int MaximumBytes = 8 * 1024;
    public const string PolicyCanonical =
        "suavo-agent-observation-policy-v1|scope:approved_pioneerrx_windows_only|" +
        "raw_frames:persistent_storage_forbidden|cloud:phi_free_aggregates_only|" +
        "input:ask_first|pause:observation_and_actuation_off|disclosure:required";
    public const string PolicyDigest =
        "6ee0ef2906c240f05e5602c97edc99595b25fc215d45a97ae0d29fb4cefccd11";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8,
        WriteIndented = false,
    };

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SuavoAgent",
        ObservationActivationAuthority.StateDirectoryName,
        FileName);

    public static ObservationActivationIdentity? LoadProduction() => Load(DefaultPath());

    public static ObservationActivationIdentity? Load(string path)
    {
        byte[] bytes = Array.Empty<byte>();
        try
        {
            AssertCompiledPolicy();
            if (!File.Exists(path)) return null;
            var info = new FileInfo(path);
            if (info.Length is <= 0 or > MaximumBytes) return null;
            bytes = File.ReadAllBytes(path);
            if (bytes.Length is <= 0 or > MaximumBytes) return null;
            var document = JsonSerializer.Deserialize<ObservationActivationIdentityDocument>(
                bytes,
                JsonOptions);
            if (document is null || document.SchemaVersion != CurrentSchemaVersion ||
                !CanonicalUuid(document.AgentId) || !CanonicalUuid(document.WorkstationId) ||
                !CanonicalUuid(document.PharmacyId) ||
                !SafeToken(document.MachineFingerprint, 256) ||
                !LowerHex64(document.DeviceKeyId) ||
                !ReleaseShape().IsMatch(document.ReleaseCohort ?? string.Empty) ||
                !FixedEquals(document.PolicyDigest, PolicyDigest) ||
                !FixedEquals(document.AgentId, document.WorkstationId))
                return null;

            var stampedRelease = ResolveStampedRelease(document.ReleaseCohort!);
            if (!FixedEquals(stampedRelease, document.ReleaseCohort))
                return null;
            return new(
                document.AgentId,
                document.WorkstationId,
                document.PharmacyId,
                document.MachineFingerprint,
                document.DeviceKeyId,
                document.ReleaseCohort!,
                document.PolicyDigest);
        }
        catch (Exception ex) when (ex is
            IOException or UnauthorizedAccessException or JsonException or
            NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public static string Serialize(ObservationActivationIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        AssertCompiledPolicy();
        if (!string.Equals(identity.PolicyDigest, PolicyDigest, StringComparison.Ordinal))
            throw new ArgumentException("Observation policy digest is not the compiled policy.", nameof(identity));
        return JsonSerializer.Serialize(
            new ObservationActivationIdentityDocument(
                CurrentSchemaVersion,
                identity.AgentId,
                identity.WorkstationId,
                identity.PharmacyId,
                identity.MachineFingerprint,
                identity.DeviceKeyId,
                identity.ReleaseCohort,
                identity.PolicyDigest),
            JsonOptions);
    }

    public static string ResolveStampedRelease(string configuredFallback)
    {
        var informational = typeof(ObservationActivationIdentityStore).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        var normalized = informational?.Split('+', 2)[0].TrimStart('v', 'V');
        return string.IsNullOrWhiteSpace(normalized) || normalized == "0.0.0"
            ? configuredFallback
            : normalized;
    }

    public static void AssertCompiledPolicy()
    {
        var digest = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(PolicyCanonical))).ToLowerInvariant();
        if (!FixedEquals(digest, PolicyDigest))
            throw new InvalidOperationException("Compiled observation policy digest mismatch.");
    }

    private static bool SafeToken(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum &&
        value.All(character => char.IsAscii(character) && !char.IsControl(character) && character != '|');

    private static bool LowerHex64(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool CanonicalUuid(string? value) =>
        value is { Length: 36 } && Guid.TryParseExact(value, "D", out var parsed) &&
        string.Equals(parsed.ToString("D"), value, StringComparison.Ordinal);

    private static bool FixedEquals(string? left, string? right)
    {
        if (left is null || right is null || left.Length != right.Length ||
            !left.All(char.IsAscii) || !right.All(char.IsAscii))
            return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(left),
            Encoding.ASCII.GetBytes(right));
    }

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9._-]{0,62}[a-z0-9])?$")]
    private static partial Regex ReleaseShape();

    private sealed record ObservationActivationIdentityDocument(
        int SchemaVersion,
        string AgentId,
        string WorkstationId,
        string PharmacyId,
        string MachineFingerprint,
        string DeviceKeyId,
        string ReleaseCohort,
        string PolicyDigest);
}
