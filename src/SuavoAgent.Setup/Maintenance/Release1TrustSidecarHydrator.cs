using System.Net;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Contracts.Security;

namespace SuavoAgent.Setup.Maintenance;

internal sealed record Release1TrustSidecarHydrationResult(
    bool Succeeded,
    string Code,
    SignedReleaseCohortEvidence? Evidence = null);

/// <summary>
/// Retrieves only the five detached, PHI-negative trust sidecars required to
/// prove the MSI-installed binaries. The private repository remains private;
/// the paired workstation uses its exact-request HMAC credential against the
/// Suavo release proxy and independently verifies both signing layers locally.
/// </summary>
internal static class Release1TrustSidecarHydrator
{
    private const string Endpoint = "/api/agent/release1/sidecar";
    private static readonly IReadOnlyDictionary<string, int> FixedAssetLimits =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [MaintenanceContract.ReleaseChecksumsFileName] = 1024 * 1024,
            [MaintenanceContract.ReleaseChecksumsSignatureFileName] = 1024,
            [MaintenanceContract.FieldReleaseReceiptFileName] = 64 * 1024,
        };

    internal static async Task<Release1TrustSidecarHydrationResult> HydrateAsync(
        SetupConfig config,
        string installDirectory,
        string dataDirectory,
        CancellationToken cancellationToken,
        HttpMessageHandler? handler = null,
        Func<string, string, SignedReleaseCohortValidation>? validateInstalled = null,
        Func<string, string, string, SignedReleaseCohortValidation>? validateStaged = null,
        string? protectedStageRoot = null,
        Action<string>? beforePublish = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!BinaryDownloader.IsValidReleaseTag(config.ReleaseTag) ||
            string.IsNullOrWhiteSpace(config.ApiKey))
            return new(false, "release_identity_invalid");
        if (!TryCloudOrigin(config.CloudUrl, out var origin))
            return new(false, "cloud_origin_invalid");

        validateInstalled ??= SignedReleaseCohortValidator.Validate;
        validateStaged ??= SignedReleaseCohortValidator.Validate;
        var existing = validateInstalled(
            installDirectory,
            config.ReleaseTag);
        if (existing.IsValid && existing.Evidence is not null)
            return new(true, "already_hydrated", existing.Evidence);

        string? proofRoot = null;
        string? stage = null;
        try
        {
            var assets = AssetLimits(config.ReleaseTag);
            _ = SafeDirectory(dataDirectory);
            var installRoot = SafeDirectory(installDirectory);
            proofRoot = Release1MsiInstallMarkerStore.RequireProtectedProofDirectory(
                protectedStageRoot ?? Release1MsiInstallMarkerStore.DefaultProofDirectory());
            stage = Path.Combine(
                proofRoot,
                "release1-sidecars-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            Release1MsiInstallMarkerStore.ProtectProofDirectory(proofRoot);
            VerifyProtectedStage(proofRoot, stage, []);
            var stagedPaths = new List<string>(assets.Count);
            handler ??= new HttpClientHandler { AllowAutoRedirect = false };
            if (handler is HttpClientHandler httpHandler)
                httpHandler.AllowAutoRedirect = false;
            using var http = new HttpClient(handler, disposeHandler: true)
            {
                BaseAddress = new Uri(origin!, "/"),
                Timeout = TimeSpan.FromSeconds(30),
            };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "SuavoAgent-Setup/1.0 (+https://suavollc.com)");
            var signer = new AgentRequestSigner(config.ApiKey);

            foreach (var (asset, maximumBytes) in assets)
            {
                VerifyProtectedStage(proofRoot, stage, stagedPaths);
                var target = Path.Combine(stage, asset);
                var pathAndQuery = Endpoint +
                    "?releaseTag=" + Uri.EscapeDataString(config.ReleaseTag) +
                    "&asset=" + Uri.EscapeDataString(asset);
                using var request = new HttpRequestMessage(HttpMethod.Get, pathAndQuery);
                signer.ApplyHeaders(request, string.Empty);
                using var response = await http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                if (response.StatusCode is >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest)
                    return new(false, "sidecar_redirect_rejected");
                if (!response.IsSuccessStatusCode)
                    return new(false, "sidecar_http_rejected");
                var bytes = await ReadBoundedAsync(
                    response.Content,
                    maximumBytes,
                    cancellationToken).ConfigureAwait(false);
                WriteNew(target, bytes);
                stagedPaths.Add(target);
                Release1MsiInstallMarkerStore.ProtectProofDirectory(proofRoot);
                VerifyProtectedStage(proofRoot, stage, stagedPaths);
            }

            VerifyProtectedStage(proofRoot, stage, stagedPaths);
            var staged = validateStaged(
                installRoot,
                stage,
                config.ReleaseTag);
            if (!staged.IsValid || staged.Evidence is null)
                return new(false, "sidecar_trust_rejected:" + staged.Code);

            beforePublish?.Invoke(stage);
            VerifyProtectedStage(proofRoot, stage, stagedPaths);
            var unchanged = validateStaged(
                installRoot,
                stage,
                config.ReleaseTag);
            if (!unchanged.IsValid || unchanged.Evidence is null)
                return new(false, "sidecar_stage_changed:" + unchanged.Code);

            PublishWithRollback(
                installRoot,
                proofRoot,
                stage,
                assets.Keys,
                () => validateInstalled(
                    installRoot,
                    config.ReleaseTag));
            var installed = validateInstalled(
                installRoot,
                config.ReleaseTag);
            return installed.IsValid && installed.Evidence is not null
                ? new(true, "hydrated", installed.Evidence)
                : new(false, "sidecar_publish_unverified");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            HttpRequestException or TaskCanceledException or IOException or
            UnauthorizedAccessException or InvalidDataException or
            System.ComponentModel.Win32Exception or System.Security.SecurityException or
            System.Security.Cryptography.CryptographicException or
            ArgumentException)
        {
            return new(false, "sidecar_hydration_failed");
        }
        finally
        {
            try
            {
                if (proofRoot is not null && stage is not null)
                    DeleteProtectedStage(proofRoot, stage);
            }
            catch { }
        }
    }

    private static IReadOnlyDictionary<string, int> AssetLimits(string releaseTag)
    {
        var values = new Dictionary<string, int>(FixedAssetLimits, StringComparer.Ordinal)
        {
            [$"update-manifest-{releaseTag}.txt"] = 128 * 1024,
            [$"update-manifest-{releaseTag}.sig"] = 1024,
        };
        return values;
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long declared &&
            (declared <= 0 || declared > maximumBytes))
            throw new InvalidDataException("Release sidecar declared an invalid size.");
        await using var source = await content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var destination = new MemoryStream(Math.Min(maximumBytes, 16 * 1024));
        var buffer = new byte[Math.Min(maximumBytes, 8 * 1024)];
        var total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)
                   .ConfigureAwait(false)) > 0)
        {
            if (total > maximumBytes - read)
                throw new InvalidDataException(
                    "Release sidecar exceeded its streaming size limit.");
            destination.Write(buffer, 0, read);
            total += read;
        }
        if (total == 0)
            throw new InvalidDataException("Release sidecar was empty.");
        return destination.ToArray();
    }

    private static void PublishWithRollback(
        string installDirectory,
        string proofRoot,
        string stageDirectory,
        IEnumerable<string> assetNames,
        Func<SignedReleaseCohortValidation> validatePublished)
    {
        var assets = assetNames.ToArray();
        var stageFiles = assets
            .Select(asset => Path.Combine(stageDirectory, asset))
            .ToList();
        var transaction = Guid.NewGuid().ToString("N");
        var backups = new Dictionary<string, string?>(StringComparer.Ordinal);
        var published = new List<string>();
        try
        {
            VerifyProtectedStage(proofRoot, stageDirectory, stageFiles);
            foreach (var asset in assets)
            {
                var source = Path.Combine(stageDirectory, asset);
                var destination = Path.Combine(installDirectory, asset);
                if (File.Exists(destination))
                {
                    EnsureRegularFile(destination);
                    var backup = Path.Combine(
                        stageDirectory,
                        "backup-" + transaction + "-" + asset);
                    File.Copy(destination, backup, overwrite: false);
                    backups[asset] = backup;
                    stageFiles.Add(backup);
                    Release1MsiInstallMarkerStore.ProtectProofDirectory(proofRoot);
                    VerifyProtectedStage(proofRoot, stageDirectory, stageFiles);
                }
                else
                {
                    backups[asset] = null;
                }

                VerifyProtectedStage(proofRoot, stageDirectory, stageFiles);
                var temporary = destination + ".tmp-" + transaction;
                File.Copy(source, temporary, overwrite: false);
                File.Move(temporary, destination, overwrite: true);
                published.Add(asset);
            }

            VerifyProtectedStage(proofRoot, stageDirectory, stageFiles);
            var validation = validatePublished();
            if (!validation.IsValid)
                throw new InvalidDataException(
                    "Published release sidecars failed verification: " +
                    validation.Code);
        }
        catch (Exception publishFailure)
        {
            Exception? rollbackFailure = null;
            foreach (var asset in published.AsEnumerable().Reverse())
            {
                var destination = Path.Combine(installDirectory, asset);
                try
                {
                    if (backups[asset] is { } backup)
                    {
                        VerifyProtectedStage(proofRoot, stageDirectory, stageFiles);
                        File.Copy(backup, destination, overwrite: true);
                    }
                    else if (File.Exists(destination))
                        File.Delete(destination);
                }
                catch (Exception exception) when (exception is
                    IOException or UnauthorizedAccessException or InvalidDataException or
                    ArgumentException)
                {
                    rollbackFailure ??= exception;
                }
            }
            if (rollbackFailure is not null)
                throw new IOException(
                    "Release sidecar rollback failed closed.",
                    new AggregateException(publishFailure, rollbackFailure));
            throw;
        }
        finally
        {
            foreach (var asset in assets)
            {
                try
                {
                    var temporary = Path.Combine(
                        installDirectory,
                        asset + ".tmp-" + transaction);
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
                catch { }
            }
        }
    }

    private static void VerifyProtectedStage(
        string proofRoot,
        string stageDirectory,
        IEnumerable<string> expectedFiles)
    {
        var files = expectedFiles
            .Select(Path.GetFullPath)
            .Distinct(OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal)
            .ToArray();
        var actual = Directory.EnumerateFileSystemEntries(stageDirectory)
            .Select(Path.GetFullPath)
            .ToHashSet(OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
        if (actual.Count != files.Length || !files.All(actual.Contains))
            throw new InvalidDataException(
                "Protected release sidecar stage contains an unexpected object.");
        Release1MsiInstallMarkerStore.VerifyProtectedProofObjects(
            proofRoot,
            [stageDirectory],
            files,
            maximumFileBytes: 1024 * 1024);
    }

    private static void DeleteProtectedStage(string proofRoot, string stageDirectory)
    {
        if (!Directory.Exists(stageDirectory)) return;
        Release1MsiInstallMarkerStore.ProtectProofDirectory(proofRoot);
        var files = Directory.EnumerateFileSystemEntries(stageDirectory).ToArray();
        if (files.Any(path => Directory.Exists(path)))
            throw new InvalidDataException(
                "Protected release sidecar cleanup found an unexpected directory.");
        VerifyProtectedStage(proofRoot, stageDirectory, files);
        foreach (var file in files) File.Delete(file);
        Directory.Delete(stageDirectory, recursive: false);
    }

    private static bool TryCloudOrigin(string value, out Uri? origin)
    {
        origin = null;
        if (!Uri.TryCreate(value.TrimEnd('/'), UriKind.Absolute, out var candidate) ||
            candidate.Scheme != Uri.UriSchemeHttps ||
            !candidate.IsDefaultPort ||
            !string.IsNullOrEmpty(candidate.UserInfo) ||
            !string.IsNullOrEmpty(candidate.Query) ||
            !string.IsNullOrEmpty(candidate.Fragment) ||
            candidate.AbsolutePath is not ("" or "/") ||
            !string.Equals(candidate.Host, "suavollc.com", StringComparison.OrdinalIgnoreCase))
            return false;
        origin = new Uri(candidate.GetLeftPart(UriPartial.Authority), UriKind.Absolute);
        return true;
    }

    private static string SafeDirectory(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
            throw new InvalidDataException("Release sidecar directory is invalid.");
        var directory = new DirectoryInfo(Path.GetFullPath(value));
        if (!directory.Exists || directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Release sidecar directory is unavailable.");
        return directory.FullName.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
    }

    private static void EnsureRegularFile(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length <= 0 ||
            file.Attributes.HasFlag(FileAttributes.Directory) ||
            file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Existing release sidecar is untrusted.");
    }

    private static void WriteNew(string path, byte[] bytes)
    {
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            16 * 1024,
            FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }
}
