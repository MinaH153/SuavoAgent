using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SuavoAgent.Installer.Metadata;

public sealed record InstallerMetadataRequest(
    string Version,
    string InstalledAtUtc,
    string OutputDirectory,
    IReadOnlyDictionary<string, string> BinaryPaths);

public sealed record InstallerMetadataResult(
    string ManifestPath,
    string InstallStatePath);

/// <summary>
/// Produces the two non-PHI integrity files consumed by the Broker and the native
/// maintenance host. Output depends only on explicit release inputs and payload bytes.
/// </summary>
public static partial class InstallerMetadataGenerator
{
    public const string ManifestFileName = "binaries.manifest";
    public const string InstallStateFileName = "install-state.json";

    public static readonly IReadOnlyList<string> InstalledCohort =
    [
        "SuavoAgent.Core.exe",
        "SuavoAgent.Broker.exe",
        "SuavoAgent.Helper.exe",
        "SuavoAgent.Watchdog.exe",
        "SuavoAgent.Maintenance.exe",
    ];

    public static InstallerMetadataResult Generate(InstallerMetadataRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);

        var manifestEntries = InstalledCohort.ToDictionary(
            static name => name,
            name => ComputeSha256(request.BinaryPaths[name]),
            StringComparer.Ordinal);
        var manifestJson = Serialize(manifestEntries);
        var installStateJson = Serialize(new InstallState(
            SchemaVersion: 1,
            InstallerKind: "native-maintenance-bridge",
            Version: request.Version,
            MaintenanceExecutable: "SuavoAgent.Maintenance.exe",
            InstalledCohort: InstalledCohort,
            InstalledAtUtc: request.InstalledAtUtc));

        Directory.CreateDirectory(request.OutputDirectory);
        var manifestPath = Path.Combine(request.OutputDirectory, ManifestFileName);
        var installStatePath = Path.Combine(request.OutputDirectory, InstallStateFileName);
        WriteAtomic(manifestPath, manifestJson);
        WriteAtomic(installStatePath, installStateJson);
        return new InstallerMetadataResult(manifestPath, installStatePath);
    }

    private static void Validate(InstallerMetadataRequest request)
    {
        if (!ThreePartVersion().IsMatch(request.Version) ||
            !Version.TryParse(request.Version, out var parsedVersion) ||
            parsedVersion.Major > byte.MaxValue ||
            parsedVersion.Minor > byte.MaxValue ||
            parsedVersion.Build > ushort.MaxValue)
        {
            throw new ArgumentException(
                "Installer version must use major.minor.patch with major/minor <= 255 " +
                "and patch <= 65535.",
                nameof(request));
        }
        if (!DateTimeOffset.TryParseExact(
                request.InstalledAtUtc,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsedTimestamp) ||
            parsedTimestamp.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Installer metadata timestamp must be an explicit round-trip UTC timestamp.",
                nameof(request));
        }
        if (string.IsNullOrWhiteSpace(request.OutputDirectory))
            throw new ArgumentException("Metadata output directory is required.", nameof(request));
        if (request.BinaryPaths.Count != InstalledCohort.Count ||
            InstalledCohort.Any(name => !request.BinaryPaths.ContainsKey(name)) ||
            request.BinaryPaths.Keys.Any(name => !InstalledCohort.Contains(name, StringComparer.Ordinal)))
        {
            throw new ArgumentException(
                "The exact five-executable installed cohort is required.",
                nameof(request));
        }
        var missing = InstalledCohort
            .Where(name => !File.Exists(request.BinaryPaths[name]))
            .ToArray();
        if (missing.Length != 0)
            throw new FileNotFoundException(
                $"Installer payload is incomplete: {string.Join(", ", missing)}");
    }

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, JsonOptions) + "\n";

    private static void WriteAtomic(string path, string value)
    {
        var temporaryPath = path + ".tmp";
        File.WriteAllText(
            temporaryPath,
            value,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    [GeneratedRegex(@"^[0-9]+\.[0-9]+\.[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ThreePartVersion();

    private sealed record InstallState(
        int SchemaVersion,
        string InstallerKind,
        string Version,
        string MaintenanceExecutable,
        IReadOnlyList<string> InstalledCohort,
        string InstalledAtUtc);
}
