using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SuavoAgent.Contracts.Reasoning;

namespace SuavoAgent.Setup.Gui.Services;

/// <summary>
/// Installs the on-device brain DURING install (the "Installing the brain" phase):
/// downloads the repository-signed LLamaSharp CPU backend package + the Qwen3
/// GGUF, SHA256-verifies both, and lands the exact universal Windows DLL set
/// where the baked Agent:Reasoning
/// config points — so the agent boots with the brain already present and reports
/// brainReady on its first heartbeat.
///
/// The model and native package are prepared in a private same-volume staging
/// directory, fully bounded and verified, and then activated with one directory
/// rename into a content-addressed cohort. The currently running Core never sees
/// partial or incompatible reasoning files.
/// </summary>
internal static partial class BrainInstaller
{
    // The GGUF is ~1.3 GB; a pharmacy DSL line at ~2 MB/s needs ~11 min. Bound it
    // so a stalled link can't wedge the installer forever (cancel works regardless).
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(30);
    internal const long MaxModelBytes = BrainCohortContract.MaxModelBytes;
    internal const long MaxNativeZipBytes = BrainCohortContract.MaxNativePackageBytes;
    internal const long MaxNativeUncompressedBytes =
        BrainNativePackageExtractor.MaxArchiveUncompressedBytes;
    internal const long MaxNativeEntryBytes = BrainNativePackageExtractor.MaxEntryBytes;
    internal const int MaxNativeEntries = BrainNativePackageExtractor.MaxArchiveEntries;
    internal const int MaxManifestBytes = 4 * 1024 * 1024;

    private const int ManifestSchemaVersion = InstalledBrainCohortVerifier.ManifestSchemaVersion;
    private const string ManifestFileName = InstalledBrainCohortVerifier.ManifestFileName;
    private const string NativePackageFileName = InstalledBrainCohortVerifier.NativePackageFileName;

    /// <summary>
    /// Returns true when the brain fully landed (libs extracted + model verified at
    /// its final path). False means no cohort was activated and Setup must not
    /// activate the configured Core cohort.
    /// Progress reports 0-100 over the combined byte budget (libs + model).
    /// </summary>
    public static async Task<bool> InstallAsync(
        AgentReasoningConfig reasoning,
        string dataDir,
        IProgress<int>? percent,
        CancellationToken ct,
        HttpMessageHandler? handler = null,
        IReadOnlyDictionary<string, string>? trustedPublisherKeys = null,
        DateTimeOffset? verificationTime = null,
        Func<bool>? repairAuthorized = null,
        Func<string, long>? availableBytes = null)
    {
        if (!reasoning.IsProvisionable) return false;

        var publisherKeys = trustedPublisherKeys ??
                            BrainCohortContract.ProductionTrustedPublisherKeys;
        DateTimeOffset Now() => verificationTime ?? DateTimeOffset.UtcNow;
        if (!TryValidate(
                reasoning,
                publisherKeys,
                Now(),
                out var modelLimit,
                out var nativeLimit,
                out var publisherCanonical))
            return false;
        var cohortRoot = reasoning.GetBrainCohortRoot(dataDir);
        var cohortsRoot = Path.GetDirectoryName(cohortRoot)!;
        var stageRoot = cohortRoot + ".staging-" + Guid.NewGuid().ToString("N");

        using var http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = DownloadTimeout;
        // HuggingFace serves a ~1 KB HTML error page (not the file) without a User-Agent, which then
        // fails the SHA check — so a UA is required for HF-direct GGUF hosting. Harmless elsewhere.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SuavoAgent/1.0 (+https://suavollc.com)");

        try
        {
            Directory.CreateDirectory(cohortsRoot);
            if (!CleanupAbandonedStages(cohortsRoot))
                return false;
            var canRepair = repairAuthorized ?? IsAdministratorForRepair;
            if (!CleanupAbandonedQuarantines(cohortsRoot, canRepair))
                return false;

            // Already present (reinstall over a provisioned box)? Re-prove the
            // exact immutable cohort rather than trusting file existence.
            if (Directory.Exists(cohortRoot))
            {
                var existing = ProtectCohort(cohortRoot)
                    ? await VerifyCohortDetailedAsync(
                        cohortRoot,
                        reasoning,
                        publisherKeys,
                        Now(),
                        ct)
                    : null;
                if (existing is { IsValid: true })
                {
                    percent?.Report(100);
                    return true;
                }
                if (!canRepair() ||
                    !BrainDiskSpaceGate.HasDataVolumeCapacity(
                        dataDir,
                        reasoning,
                        publisherKeys,
                        Now(),
                        forceFullProvisioning: true,
                        availableBytes: availableBytes) ||
                    !QuarantineInvalidCohort(
                        cohortRoot,
                        reasoning.BrainCohortId(),
                        existing?.Code ?? "cohort_acl_invalid",
                        cohortsRoot,
                        Now()))
                    return false;
            }
            else if (!BrainDiskSpaceGate.HasDataVolumeCapacity(
                         dataDir,
                         reasoning,
                         publisherKeys,
                         Now(),
                         forceFullProvisioning: true,
                         availableBytes: availableBytes))
                return false;

            Directory.CreateDirectory(stageRoot);
            var modelPath = Path.Combine(
                stageRoot,
                "model",
                AgentReasoningConfig.SafeFileNameFromUrl(reasoning.ModelUrl, "model.gguf"));
            var nativeDir = Path.Combine(stageRoot, "native");
            var libsTmp = Path.Combine(stageRoot, NativePackageFileName);

            var libsBytes = reasoning.NativeLibsSizeBytes ?? nativeLimit;
            var modelBytes = reasoning.ModelSizeBytes ?? modelLimit;
            var totalBytes = libsBytes + modelBytes;

            // Native package: bounded streaming download, package hash proof,
            // then traversal/symlink/zip-bomb-safe extraction into the stage.
            var libsDownloaded = await DownloadWithProgressAsync(
                http, reasoning.NativeLibsUrl, libsTmp, reasoning.NativeLibsSha256,
                nativeLimit,
                reasoning.NativeLibsSizeBytes,
                done => percent?.Report(Percent(done, totalBytes)), ct);
            if (!libsDownloaded) return false;
            var nativePackage = await BrainNativePackageExtractor.ExtractAsync(
                    libsTmp,
                    nativeDir,
                    reasoning.NativePackageKind,
                    ct)
                .ConfigureAwait(false);
            var nativeFiles = nativePackage.NativeFiles;
            if (!nativePackage.IsValid || nativeFiles is null) return false;

            // Model: bounded streaming download + hash proof into the same stage.
            Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
            var modelDownloaded = await DownloadWithProgressAsync(
                http, reasoning.ModelUrl, modelPath, reasoning.ModelSha256,
                modelLimit,
                reasoning.ModelSizeBytes,
                done => percent?.Report(Percent(libsBytes + done, totalBytes)), ct);
            if (!modelDownloaded) return false;

            var manifest = new InstalledBrainCohortManifest(
                ManifestSchemaVersion,
                reasoning.BrainCohortId(),
                publisherCanonical,
                reasoning.PublisherManifest(),
                Path.GetFileName(modelPath),
                new FileInfo(modelPath).Length,
                NormalizeSha(reasoning.ModelSha256),
                new FileInfo(libsTmp).Length,
                NormalizeSha(reasoning.NativeLibsSha256),
                nativeFiles,
                reasoning.NativePackageKind);
            await WriteManifestAsync(
                Path.Combine(stageRoot, ManifestFileName),
                manifest,
                ct);
            if (!ProtectCohort(stageRoot)) return false;
            if (!await VerifyCohortAsync(
                    stageRoot,
                    reasoning,
                    publisherKeys,
                    Now(),
                    ct))
                return false;

            // Same-volume directory rename is the only activation point. The
            // baked appsettings targets this content-addressed final path.
            try
            {
                Directory.Move(stageRoot, cohortRoot);
            }
            catch (IOException) when (Directory.Exists(cohortRoot))
            {
                if (!ProtectCohort(cohortRoot)) return false;
                if (!await VerifyCohortAsync(
                        cohortRoot,
                        reasoning,
                        publisherKeys,
                        Now(),
                        ct))
                    return false;
            }
            if (!VerifyCohortAcl(cohortRoot)) return false;
            percent?.Report(100);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
        finally
        {
            DeleteStage(stageRoot);
        }
    }

    /// <summary>Streams a download to disk computing SHA256 incrementally (no second
    /// read of a 1.3 GB file). Returns false on any mismatch or HTTP failure.</summary>
    private static async Task<bool> DownloadWithProgressAsync(
        HttpClient http, string url, string destination, string expectedSha256,
        long maxBytes,
        long? expectedBytes,
        Action<long> onBytes,
        CancellationToken ct)
    {
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode) return false;
        if (resp.Content.Headers.ContentLength is long contentLength &&
            (contentLength <= 0 || contentLength > maxBytes ||
             expectedBytes is long exact && contentLength != exact))
            return false;

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long done = 0;

        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = new FileStream(
                         destination,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         1 << 20,
                         FileOptions.WriteThrough))
        {
            var buffer = new byte[1 << 20];
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                if (done > maxBytes - read ||
                    expectedBytes is long expected && done > expected - read)
                {
                    Cleanup(destination);
                    return false;
                }
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                sha.AppendData(buffer, 0, read);
                done += read;
                onBytes(done);
            }
            await dst.FlushAsync(ct);
            dst.Flush(flushToDisk: true);
        }

        var actual = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
        if (done <= 0 || expectedBytes is long exactBytes && done != exactBytes ||
            !FixedHashEquals(actual, NormalizeSha(expectedSha256)))
        {
            Cleanup(destination);
            return false;
        }
        return true;
    }

    private static async Task<bool> VerifyCohortAsync(
        string cohortRoot,
        AgentReasoningConfig reasoning,
        IReadOnlyDictionary<string, string> trustedPublisherKeys,
        DateTimeOffset now,
        CancellationToken ct)
        => (await VerifyCohortDetailedAsync(
            cohortRoot,
            reasoning,
            trustedPublisherKeys,
            now,
            ct)).IsValid;

    private static async Task<InstalledBrainCohortVerification> VerifyCohortDetailedAsync(
        string cohortRoot,
        AgentReasoningConfig reasoning,
        IReadOnlyDictionary<string, string> trustedPublisherKeys,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var verified = await InstalledBrainCohortVerifier.VerifyAsync(
            cohortRoot,
            reasoning.PublisherManifest(),
            trustedPublisherKeys,
            now,
            ct);
        if (!verified.IsValid) return verified;
        if (!verified.AuthorizationRefreshRequired) return verified;

        var renewed = InstalledBrainCohortVerifier.RenewAuthorization(
            verified,
            reasoning.PublisherManifest());
        if (!await ReplaceManifestAsync(cohortRoot, renewed, ct))
            return new(false, "authorization_refresh_failed");
        var reproved = await InstalledBrainCohortVerifier.VerifyAsync(
            cohortRoot,
            reasoning.PublisherManifest(),
            trustedPublisherKeys,
            now,
            ct);
        return reproved.IsValid && !reproved.AuthorizationRefreshRequired
            ? reproved
            : new(false, reproved.IsValid
                ? "authorization_refresh_incomplete"
                : reproved.Code);
    }

    private static async Task WriteManifestAsync(
        string path,
        InstalledBrainCohortManifest manifest,
        CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            manifest,
            InstalledBrainCohortVerifier.ManifestJson);
        if (bytes.Length is <= 0 or > MaxManifestBytes)
            throw new InvalidDataException("Brain cohort manifest exceeds its safe bound.");
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            16 * 1024,
            FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
        stream.Flush(flushToDisk: true);
    }

    private static async Task<bool> ReplaceManifestAsync(
        string cohortRoot,
        InstalledBrainCohortManifest manifest,
        CancellationToken ct)
    {
        var temp = Path.Combine(
            cohortRoot,
            ".manifest-new-" + Guid.NewGuid().ToString("N"));
        try
        {
            await WriteManifestAsync(temp, manifest, ct);
            File.Move(
                temp,
                Path.Combine(cohortRoot, ManifestFileName),
                overwrite: true);
            return ProtectCohort(cohortRoot);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
        finally
        {
            Cleanup(temp);
        }
    }

    private static bool TryValidate(
        AgentReasoningConfig reasoning,
        IReadOnlyDictionary<string, string> trustedPublisherKeys,
        DateTimeOffset now,
        out long modelLimit,
        out long nativeLimit,
        out string publisherCanonical)
    {
        modelLimit = reasoning.ModelSizeBytes ?? 0;
        nativeLimit = reasoning.NativeLibsSizeBytes ?? 0;
        publisherCanonical = string.Empty;
        var publisher = reasoning.ValidatePublisher(trustedPublisherKeys, now);
        if (!publisher.IsValid || publisher.Canonical is null) return false;
        publisherCanonical = publisher.Canonical;
        return reasoning.IsProvisionable &&
               IsHttps(reasoning.ModelUrl) &&
               IsHttps(reasoning.NativeLibsUrl) &&
               IsSha256(reasoning.ModelSha256) &&
               IsSha256(reasoning.NativeLibsSha256) &&
               modelLimit is > 0 and <= MaxModelBytes &&
               nativeLimit is > 0 and <= MaxNativeZipBytes;
    }

    private static bool CleanupAbandonedStages(string cohortsRoot)
    {
        try
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(
                         cohortsRoot,
                         "*.manifest-new-*",
                         SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(path);
                const string markerText = ".manifest-new-";
                var marker = name.IndexOf(markerText, StringComparison.Ordinal);
                if (marker != 64 || name.Length != 64 + markerText.Length + 32 ||
                    !IsLowerHex(name.AsSpan(0, 64)) ||
                    !IsLowerHex(name.AsSpan(marker + markerText.Length, 32)))
                    continue;
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.Directory) != 0) return false;
                File.Delete(path);
            }
            foreach (var cohort in Directory.EnumerateDirectories(
                         cohortsRoot,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                var cohortName = Path.GetFileName(cohort);
                if (cohortName.Length != 64 || !IsLowerHex(cohortName.AsSpan()))
                    continue;
                if (IsReparse(cohort)) return false;
                foreach (var path in Directory.EnumerateFileSystemEntries(
                             cohort,
                             ".manifest-new-*",
                             SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileName(path);
                    const string prefix = ".manifest-new-";
                    if (name.Length != prefix.Length + 32 ||
                        !name.StartsWith(prefix, StringComparison.Ordinal) ||
                        !IsLowerHex(name.AsSpan(prefix.Length)))
                        continue;
                    var attributes = File.GetAttributes(path);
                    if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                        return false;
                    File.Delete(path);
                }
            }
            foreach (var path in Directory.EnumerateFileSystemEntries(
                         cohortsRoot,
                         "*.staging-*",
                         SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(path);
                var marker = name.IndexOf(".staging-", StringComparison.Ordinal);
                if (marker != 64 || name.Length != 64 + 9 + 32 ||
                    !IsLowerHex(name.AsSpan(0, 64)) ||
                    !IsLowerHex(name.AsSpan(marker + 9, 32)))
                    continue;
                if (!DeleteStage(path)) return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool DeleteStage(string path)
    {
        try
        {
            FileAttributes rootAttributes;
            try { rootAttributes = File.GetAttributes(path); }
            catch (FileNotFoundException) { return true; }
            catch (DirectoryNotFoundException) { return true; }
            var rootIsDirectory = (rootAttributes & FileAttributes.Directory) != 0;
            if ((rootAttributes & FileAttributes.ReparsePoint) != 0)
            {
                if (rootIsDirectory) Directory.Delete(path, recursive: false);
                else File.Delete(path);
            }
            else if (rootIsDirectory)
                DeleteTreeWithoutFollowingReparsePoints(
                    path,
                    MaxNativeEntries * 3 + 32);
            else
                return false;
            return !File.Exists(path) && !Directory.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    private static IReadOnlyList<string>? EnumerateTreeFilesWithoutReparse(
        string root,
        int maxEntries)
    {
        var files = new List<string>();
        var count = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                if (++count > maxEntries || IsReparse(entry)) return null;
                if (Directory.Exists(entry))
                {
                    pending.Push(entry);
                    continue;
                }
                files.Add(Path.GetRelativePath(root, entry)
                    .Replace(Path.DirectorySeparatorChar, '/'));
            }
        }
        return files;
    }

    private static void DeleteTreeWithoutFollowingReparsePoints(
        string root,
        int maxEntries)
    {
        var directories = new List<string> { root };
        var leaves = new List<(string Path, bool IsDirectory)>();
        var pending = new Stack<string>();
        var count = 0;
        pending.Push(root);
        while (pending.Count > 0)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                if (++count > maxEntries)
                    throw new InvalidDataException(
                        "Abandoned brain stage exceeds its bounded entry count.");
                var attributes = File.GetAttributes(entry);
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                if ((attributes & FileAttributes.ReparsePoint) != 0 || !isDirectory)
                {
                    leaves.Add((entry, isDirectory));
                    continue;
                }
                directories.Add(entry);
                pending.Push(entry);
            }
        }
        foreach (var leaf in leaves)
        {
            if (leaf.IsDirectory) Directory.Delete(leaf.Path, recursive: false);
            else File.Delete(leaf.Path);
        }
        foreach (var directory in directories
                     .OrderByDescending(value => value.Length))
            Directory.Delete(directory, recursive: false);
    }

    private static bool IsReparse(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static bool ProtectCohort(string path) =>
        !OperatingSystem.IsWindows() || BrainCohortAcl.ProtectAndVerify(path).IsValid;

    private static bool VerifyCohortAcl(string path) =>
        !OperatingSystem.IsWindows() || BrainCohortAcl.Verify(path).IsValid;

    private static bool IsHttps(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        string.IsNullOrEmpty(uri.UserInfo);

    private static bool IsSha256(string value) =>
        value is { Length: 64 } && IsHex(value.AsSpan());

    private static bool IsHex(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
            if (character is not (>= '0' and <= '9') and
                not (>= 'a' and <= 'f') and
                not (>= 'A' and <= 'F'))
                return false;
        return true;
    }

    private static bool IsLowerHex(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                return false;
        return true;
    }

    private static string NormalizeSha(string value) => value.Trim().ToLowerInvariant();

    private static bool FixedHashEquals(string left, string right) =>
        left.Length == right.Length &&
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(left),
            Encoding.ASCII.GetBytes(right));

    private static int Percent(long done, long total) =>
        total <= 0 ? 0 : (int)Math.Clamp(done * 100 / total, 0, 100);

    private static void Cleanup(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort */ }
    }
}
