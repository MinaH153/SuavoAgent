using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SuavoAgent.Contracts.Reasoning;

return Run(args);

static int Run(string[] arguments)
{
    const string optInName = "SUAVO_ALLOW_DEVELOPMENT_PEM_SIGNER";
    const string optInValue = "I_UNDERSTAND_THIS_IS_NOT_FOR_RELEASE";
    if (arguments.Length != 5 ||
        !string.Equals(arguments[0], "--development-fixture", StringComparison.Ordinal))
    {
        Console.Error.WriteLine(
            "Production brain signing requires separate non-exportable model and native HSM keys. " +
            "This tool only supports: --development-fixture <unsigned.json> <model-key.pem> " +
            "<native-key.pem> <signed.json>.");
        return 2;
    }
    if (!string.Equals(Environment.GetEnvironmentVariable(optInName), optInValue,
            StringComparison.Ordinal))
    {
        Console.Error.WriteLine(
            $"Development PEM signing refused. Set {optInName} to the documented explicit fixture opt-in.");
        return 2;
    }

    var inputPath = Path.GetFullPath(arguments[1]);
    var modelKeyPath = Path.GetFullPath(arguments[2]);
    var nativeKeyPath = Path.GetFullPath(arguments[3]);
    var outputPath = Path.GetFullPath(arguments[4]);
    try
    {
        AssertPrivateKeyPermissions(modelKeyPath);
        AssertPrivateKeyPermissions(nativeKeyPath);
        if (string.Equals(modelKeyPath, nativeKeyPath, PathComparison()))
            throw new InvalidDataException("Model and native development keys must be different files.");

        var input = new FileInfo(inputPath);
        if (!input.Exists || input.Length is <= 0 or > 32 * 1024 ||
            input.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Unsigned manifest must be a bounded regular file.");
        if (File.Exists(outputPath))
            throw new IOException("Signed manifest output already exists.");

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            MaxDepth = 8,
            WriteIndented = true,
        };
        var manifest = JsonSerializer.Deserialize<BrainCohortPublisherManifest>(
                           File.ReadAllText(inputPath, new UTF8Encoding(false, true)),
                           jsonOptions)
                       ?? throw new InvalidDataException("Unsigned manifest is empty.");
        if (manifest.SchemaVersion != BrainCohortContract.SchemaVersion ||
            !string.IsNullOrEmpty(manifest.KeyId) ||
            !string.IsNullOrEmpty(manifest.Signature) ||
            !string.IsNullOrEmpty(manifest.ModelSignature) ||
            !string.IsNullOrEmpty(manifest.NativeSignature) ||
            !manifest.ModelKeyId.StartsWith("brain-model-dev-", StringComparison.Ordinal) ||
            !manifest.NativeKeyId.StartsWith("brain-native-dev-", StringComparison.Ordinal) ||
            manifest.ModelKeyId == BrainCohortContract.ProductionModelKeyId ||
            manifest.NativeKeyId == BrainCohortContract.ProductionNativeKeyId)
            throw new InvalidDataException(
                "Development fixtures require schema v2, empty legacy/signature fields, and dev-only role key IDs.");

        using var modelSigner = ReadP256Signer(modelKeyPath);
        using var nativeSigner = ReadP256Signer(nativeKeyPath);
        var modelPublic = modelSigner.ExportSubjectPublicKeyInfo();
        var nativePublic = nativeSigner.ExportSubjectPublicKeyInfo();
        if (modelPublic.Length == nativePublic.Length &&
            CryptographicOperations.FixedTimeEquals(modelPublic, nativePublic))
            throw new CryptographicException("Model and native roles must use different keys.");

        manifest = manifest with
        {
            CohortId = BrainCohortContract.ComputeCohortId(manifest),
            ModelSignature = string.Empty,
            NativeSignature = string.Empty,
        };
        manifest = manifest with
        {
            ModelSignature = Sign(modelSigner, BrainCohortContract.BuildModelCanonical(manifest)),
            NativeSignature = Sign(nativeSigner, BrainCohortContract.BuildNativeCanonical(manifest)),
        };
        var keys = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [manifest.ModelKeyId] = Convert.ToBase64String(modelPublic),
            [manifest.NativeKeyId] = Convert.ToBase64String(nativePublic),
        };
        var validation = BrainCohortContract.Validate(manifest, keys, DateTimeOffset.UtcNow);
        if (!validation.IsValid)
            throw new InvalidDataException(
                $"Signed fixture failed publisher validation: {validation.Code}");

        WriteNewFileAtomically(
            outputPath,
            JsonSerializer.Serialize(manifest, jsonOptions));
        Console.WriteLine("Signed development brain fixture written.");
        return 0;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"Development brain fixture signing refused: {exception.Message}");
        return 1;
    }
}

static ECDsa ReadP256Signer(string path)
{
    var signer = ECDsa.Create();
    signer.ImportFromPem(File.ReadAllText(path, new UTF8Encoding(false, true)));
    if (signer.KeySize != 256)
    {
        signer.Dispose();
        throw new CryptographicException("Development signer must be ECDSA P-256.");
    }
    return signer;
}

static string Sign(ECDsa signer, string canonical)
{
    var signature = signer.SignData(
        Encoding.UTF8.GetBytes(canonical),
        HashAlgorithmName.SHA256,
        DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    if (signature.Length != 64)
        throw new CryptographicException("Development signer returned a non-P1363 signature.");
    return Convert.ToHexString(signature).ToLowerInvariant();
}

static void WriteNewFileAtomically(string outputPath, string value)
{
    var temporary = outputPath + ".tmp-" + Guid.NewGuid().ToString("N");
    try
    {
        using (var stream = new FileStream(
                   temporary,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None,
                   4096,
                   FileOptions.WriteThrough))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true))
        {
            writer.Write(value);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, outputPath, overwrite: false);
    }
    finally
    {
        try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
    }
}

static void AssertPrivateKeyPermissions(string path)
{
    var info = new FileInfo(path);
    if (!info.Exists || info.Length is <= 0 or > 64 * 1024 ||
        info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        throw new FileNotFoundException("Development private key is unavailable or redirected.", path);
    if (OperatingSystem.IsWindows()) return;
    var mode = File.GetUnixFileMode(path);
    const UnixFileMode forbidden =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
    if ((mode & forbidden) != 0)
        throw new UnauthorizedAccessException(
            "Development private key must not grant group or other permissions.");
}

static StringComparison PathComparison() =>
    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
