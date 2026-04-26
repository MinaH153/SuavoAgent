using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Serilog;
using SuavoAgent.Contracts.Vision;
using SuavoAgent.Core.Config;

namespace SuavoAgent.Helper.Vision;

/// <summary>
/// Per-user DPAPI(CurrentUser)-encrypted screen store. Windows-only — fails
///
/// CRYPTO PROVENANCE (Codex 2026-04-26 audit):
///   - Algorithm: AES (CNG provider, system-default mode — DPAPI internals)
///   - Key derivation: Windows Data Protection API, scope=CurrentUser
///   - Key storage: Windows credential store, sealed by the user's master key
///   - Per-file: ProtectedData.Protect appends an integrity MAC; tamper on
///     disk fails the Unprotect call.
///   - Recovery: NONE — losing the user profile makes every file useless.
///     This is the intended posture for ephemeral PHI-adjacent storage.
///
/// For an FDA / HIPAA dossier, the algorithm provenance lives in Windows
/// itself (DPAPI is a documented system service) — we do NOT roll our own
/// crypto here, and we deliberately don't expose key material so the
/// dossier reduction is "trust DPAPI on the host OS." If that posture is
/// ever insufficient, swap ProtectedData for AES-GCM with a key managed
/// by Windows Hello / TPM-backed cred and update this header.
/// closed on non-Windows platforms to avoid the plaintext fallback that
/// would create raw screens-at-rest in CI / development (Codex C-3).
///
/// Multi-tenant safety (Codex C-1):
///   - DPAPI scope is CurrentUser, so only the user who captured a screen
///     can decrypt it. User B on the same machine cannot Unprotect user A's
///     files even if they have filesystem read access.
///   - Directory is per-user (default: %LOCALAPPDATA%\SuavoAgent\screens\),
///     ACL-locked to SYSTEM + Administrators + the specific current user
///     SID (not the generic InteractiveSid).
///   - Directory setup is a hard readiness check — any failure refuses to
///     construct the store (Codex C-4).
///
/// Integrity (Codex C-2):
///   - Every envelope contains a SHA-256 of the PNG bytes AND the file id.
///   - LoadAsync verifies the id matches the requested filename and the
///     hash matches the bytes before returning. File-swap attacks fail.
///
/// Retention:
///   - TTL purge: files older than RetentionHours deleted
///   - Cap purge: oldest-first when count exceeds MaxStoredScreens
///   - RetentionHours=0 still means "TTL disabled" — documented.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class EncryptedScreenStore : IScreenStore
{
    private readonly VisionOptions _options;
    private readonly ILogger _logger;
    private readonly string _directory;

    public EncryptedScreenStore(IOptions<AgentOptions> options, ILogger logger)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "EncryptedScreenStore requires Windows — non-Windows hosts would " +
                "either write plaintext (HIPAA violation) or fail at runtime (Codex C-3).");
        }

        _options = options.Value.Vision;
        _logger = logger;

        if (_options.MaxStoredScreens < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(_options.MaxStoredScreens),
                _options.MaxStoredScreens,
                "VisionOptions.MaxStoredScreens must be >= 1 (Codex M-5)");
        }

        _directory = ResolveDirectory(_options);
        ValidatePath(_directory);
        EnsureDirectoryWithAclOrThrow();
    }

    public async Task<string?> StoreAsync(ScreenBytes screen, CancellationToken ct)
    {
        try
        {
            var id = $"{screen.CapturedAt:yyyyMMddTHHmmssfff}_{Guid.NewGuid():N}";
            var pngHash = Convert.ToHexString(SHA256.HashData(screen.Png)).ToLowerInvariant();

            var envelope = JsonSerializer.SerializeToUtf8Bytes(new ScreenEnvelope
            {
                Id = id,
                PngSha256 = pngHash,
                Width = screen.Width,
                Height = screen.Height,
                CapturedAt = screen.CapturedAt,
                PngBase64 = Convert.ToBase64String(screen.Png),
            });

            // CurrentUser scope: file is readable only by the user that wrote it.
            var encrypted = ProtectedData.Protect(envelope, null, DataProtectionScope.CurrentUser);

            var path = Path.Combine(_directory, $"{id}.scn");
            await File.WriteAllBytesAsync(path, encrypted, ct);

            // Best-effort purge after write so the store self-maintains.
            _ = Task.Run(() => PurgeExpiredAsync(CancellationToken.None), ct);

            return id;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "EncryptedScreenStore: store failed");
            return null;
        }
    }

    public async Task<ScreenBytes?> LoadAsync(string id, CancellationToken ct)
    {
        try
        {
            // Sanitize id against path traversal BEFORE using as a filename.
            if (string.IsNullOrWhiteSpace(id) || id.Contains('/') || id.Contains('\\') || id.Contains(".."))
            {
                _logger.Warning("EncryptedScreenStore: rejected load for id {Id} (unsafe chars)", id);
                return null;
            }

            var path = Path.Combine(_directory, $"{id}.scn");
            if (!File.Exists(path)) return null;

            var encrypted = await File.ReadAllBytesAsync(path, ct);
            var envelope = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);

            var parsed = JsonSerializer.Deserialize<ScreenEnvelope>(envelope);
            if (parsed == null) return null;

            // Codex C-2: verify id matches the filename so a swapped .scn file
            // under a different id is rejected.
            if (!string.Equals(parsed.Id, id, StringComparison.Ordinal))
            {
                _logger.Warning(
                    "EncryptedScreenStore: envelope id mismatch — filename '{FileId}' vs envelope '{EnvId}'; refusing load",
                    id, parsed.Id);
                return null;
            }

            var png = Convert.FromBase64String(parsed.PngBase64);

            // Verify payload hash matches what was stored.
            var actualHash = Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant();
            if (!string.Equals(actualHash, parsed.PngSha256, StringComparison.OrdinalIgnoreCase))
            {
                _logger.Warning(
                    "EncryptedScreenStore: payload hash mismatch for {Id}; refusing load", id);
                return null;
            }

            return new ScreenBytes(png, parsed.Width, parsed.Height, parsed.CapturedAt);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "EncryptedScreenStore: load failed for {Id}", id);
            return null;
        }
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(id) || id.Contains('/') || id.Contains('\\') || id.Contains(".."))
                return false;

            var path = Path.Combine(_directory, $"{id}.scn");
            if (!File.Exists(path)) return false;

            await TombstoneAndDeleteAsync(path, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "EncryptedScreenStore: delete failed for {Id}", id);
            return false;
        }
    }

    /// <summary>
    /// Tombstoned delete — overwrite the file with random bytes before
    /// unlinking it. NTFS leaves freed blocks intact until reused; without
    /// this overwrite a file-recovery tool could reconstruct the encrypted
    /// envelope (still DPAPI-protected, but defense-in-depth says don't
    /// leave recoverable bytes on disk for PHI-adjacent storage).
    ///
    /// Codex 2026-04-26 flagged plain File.Delete as a HIPAA defense-in-depth
    /// gap. This pattern matches the AgentStateDb.SecureDelete approach used
    /// for state.key / state.db.
    /// </summary>
    private static async Task TombstoneAndDeleteAsync(string path, CancellationToken ct)
    {
        // Open with FileShare.None so a concurrent reader can't snapshot
        // mid-overwrite. Read the size first; allocate a single random
        // buffer; rewrite the entire file with random bytes; flush; delete.
        long length;
        try
        {
            length = new FileInfo(path).Length;
        }
        catch
        {
            // File may have vanished between Exists check and Length read —
            // fall through to the Delete which will then no-op-throw.
            length = 0;
        }

        if (length > 0)
        {
            try
            {
                using var fs = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4096,
                    useAsync: true);
                var buffer = new byte[Math.Min(64 * 1024, length)];
                System.Security.Cryptography.RandomNumberGenerator.Fill(buffer);
                long written = 0;
                while (written < length && !ct.IsCancellationRequested)
                {
                    var chunk = (int)Math.Min(buffer.Length, length - written);
                    await fs.WriteAsync(buffer.AsMemory(0, chunk), ct).ConfigureAwait(false);
                    written += chunk;
                }
                await fs.FlushAsync(ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // If the overwrite fails partway, still try to delete — a
                // partially-overwritten file is no worse than a non-overwritten
                // one for our security posture.
            }
        }

        File.Delete(path);
    }

    public async Task<int> PurgeExpiredAsync(CancellationToken ct)
    {
        var removed = 0;
        try
        {
            if (!Directory.Exists(_directory)) return 0;

            var cutoff = _options.RetentionHours > 0
                ? DateTimeOffset.UtcNow.AddHours(-_options.RetentionHours)
                : (DateTimeOffset?)null;

            var files = Directory.EnumerateFiles(_directory, "*.scn")
                .Select(f => new FileInfo(f))
                .OrderBy(f => f.CreationTimeUtc)
                .ToList();

            if (cutoff.HasValue)
            {
                foreach (var f in files.ToList())
                {
                    if (ct.IsCancellationRequested) break;
                    if (f.CreationTimeUtc < cutoff.Value.UtcDateTime)
                    {
                        try { f.Delete(); removed++; files.Remove(f); }
                        catch (Exception ex)
                        {
                            _logger.Debug(ex, "Purge: delete failed for {Name}", f.Name);
                        }
                    }
                }
            }

            var max = _options.MaxStoredScreens;
            while (files.Count > max)
            {
                if (ct.IsCancellationRequested) break;
                var oldest = files[0];
                try { oldest.Delete(); removed++; }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Purge: delete failed for {Name}", oldest.Name);
                }
                files.RemoveAt(0);
            }

            await Task.CompletedTask;
            if (removed > 0)
                _logger.Information("EncryptedScreenStore: purged {Count} screen(s)", removed);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "EncryptedScreenStore: purge error");
        }
        return removed;
    }

    private static string ResolveDirectory(VisionOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.StorageDirectory))
            return options.StorageDirectory!;

        // Per-user default — %LOCALAPPDATA%\SuavoAgent\screens\
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SuavoAgent", "screens");
    }

    /// <summary>
    /// Reject paths that would break our HIPAA model: UNC shares (could be
    /// monitored by other machines), reparse points (could redirect anywhere).
    /// </summary>
    private static void ValidatePath(string directory)
    {
        // UNC rejection — no \\server\share paths
        if (directory.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Vision StorageDirectory must be a local path (UNC rejected, Codex C-4)");
        }

        // Reparse-point check — only valid if the directory exists; if it
        // doesn't exist yet, we skip this and EnsureDirectoryWithAclOrThrow
        // creates a normal directory.
        if (Directory.Exists(directory))
        {
            var info = new DirectoryInfo(directory);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new ArgumentException(
                    $"Vision StorageDirectory must not be a reparse point: {directory} (Codex C-4)");
            }
        }
    }

    /// <summary>
    /// Creates the directory if missing and applies a strict ACL (SYSTEM +
    /// Administrators + current user SID only). Any failure throws — we
    /// refuse to run with a wide-open directory (Codex C-4).
    /// </summary>
    private void EnsureDirectoryWithAclOrThrow()
    {
        Directory.CreateDirectory(_directory);

        var dirInfo = new DirectoryInfo(_directory);
        var security = dirInfo.GetAccessControl();
        security.SetAccessRuleProtection(true, false); // remove inherited

        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

        // Current user SID only — not the generic InteractiveSid. That means
        // user B on the same machine cannot see user A's screen files even if
        // they somehow gained file-system read (Codex C-1).
        //
        // Codex 2026-04-26 review: Modify includes Delete + WriteAttributes.
        // Helper genuinely needs both (it cleans up captures via
        // PurgeExpiredAsync + DeleteAsync), so we cannot drop to ReadOnly
        // here without splitting the writer/janitor across two processes.
        // Net exposure: an attacker who compromises the Helper user can
        // delete or modify their OWN captures, but not pivot to read other
        // users' captures (DPAPI(CurrentUser) makes the file unreadable
        // even if the bytes are exfiltrated). Documented + acknowledged.
        var currentUser = WindowsIdentity.GetCurrent();
        if (currentUser.User is null)
            throw new InvalidOperationException("Current user SID is unavailable — cannot lock ACL");

        // Use Modify (Read + Write + Delete) explicitly minus ChangePermissions
        // and TakeOwnership — those would let the user re-grant the ACL to
        // attacker-controlled SIDs. Restricting those is the only meaningful
        // tightening over plain Modify.
        const FileSystemRights helperRights =
            FileSystemRights.ReadAndExecute |
            FileSystemRights.Write |
            FileSystemRights.Delete |
            FileSystemRights.DeleteSubdirectoriesAndFiles;
        security.AddAccessRule(new FileSystemAccessRule(
            currentUser.User,
            helperRights,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

        dirInfo.SetAccessControl(security);
        _logger.Information(
            "EncryptedScreenStore: directory {Path} locked to SYSTEM + Admins + {User} (no ChangePermissions, no TakeOwnership)",
            _directory, currentUser.Name);
    }

    private sealed class ScreenEnvelope
    {
        public string Id { get; set; } = "";
        public string PngSha256 { get; set; } = "";
        public int Width { get; set; }
        public int Height { get; set; }
        public DateTimeOffset CapturedAt { get; set; }
        public string PngBase64 { get; set; } = "";
    }
}
