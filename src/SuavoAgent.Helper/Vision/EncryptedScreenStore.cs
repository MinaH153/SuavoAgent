using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Serilog;
using SuavoAgent.Contracts.Vision;
using SuavoAgent.Core.Config;

namespace SuavoAgent.Helper.Vision;

/// <summary>
/// DPAPI-encrypted file store for captured screens.
///
/// Layout (under VisionOptions.StorageDirectory or ProgramData\SuavoAgent\screens):
///   - {timestamp-iso}_{uuid}.scn    ← DPAPI(LocalMachine) encrypted payload
///
/// The payload is a JSON envelope containing width, height, captured_at, and
/// the PNG bytes base64-encoded. DPAPI(LocalMachine) means only this machine
/// can decrypt — exfiltration of the file alone leaks nothing.
///
/// Retention:
///   - TTL purge: anything older than VisionOptions.RetentionHours deleted
///   - Cap purge: oldest-first purge when file count exceeds MaxStoredScreens
///
/// Never throws for I/O failures — fail-closed with a warning log.
/// </summary>
public sealed class EncryptedScreenStore : IScreenStore
{
    private readonly VisionOptions _options;
    private readonly ILogger _logger;
    private readonly string _directory;

    public EncryptedScreenStore(IOptions<AgentOptions> options, ILogger logger)
    {
        _options = options.Value.Vision;
        _logger = logger;
        _directory = ResolveDirectory(_options);
        EnsureDirectoryWithAcl();
    }

    public async Task<string?> StoreAsync(ScreenBytes screen, CancellationToken ct)
    {
        if (!Directory.Exists(_directory))
        {
            _logger.Warning("EncryptedScreenStore: storage dir missing at {Path}", _directory);
            return null;
        }

        try
        {
            var id = $"{screen.CapturedAt:yyyyMMddTHHmmssfff}_{Guid.NewGuid():N}";
            var envelope = JsonSerializer.SerializeToUtf8Bytes(new ScreenEnvelope
            {
                Id = id,
                Width = screen.Width,
                Height = screen.Height,
                CapturedAt = screen.CapturedAt,
                PngBase64 = Convert.ToBase64String(screen.Png),
            });

            byte[] encrypted;
            if (OperatingSystem.IsWindows())
            {
                encrypted = ProtectedData.Protect(envelope, null, DataProtectionScope.LocalMachine);
            }
            else
            {
                // Non-Windows: no DPAPI available. Store plaintext in DEV only
                // — production is Windows-only. Log a strong warning.
                _logger.Warning("EncryptedScreenStore: DPAPI unavailable, storing UNENCRYPTED (dev only)");
                encrypted = envelope;
            }

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
            var path = Path.Combine(_directory, $"{id}.scn");
            if (!File.Exists(path)) return null;

            var encrypted = await File.ReadAllBytesAsync(path, ct);
            byte[] envelope;
            if (OperatingSystem.IsWindows())
            {
                envelope = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.LocalMachine);
            }
            else
            {
                envelope = encrypted;
            }

            var parsed = JsonSerializer.Deserialize<ScreenEnvelope>(envelope);
            if (parsed == null) return null;

            return new ScreenBytes(
                Convert.FromBase64String(parsed.PngBase64),
                parsed.Width,
                parsed.Height,
                parsed.CapturedAt);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "EncryptedScreenStore: load failed for {Id}", id);
            return null;
        }
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

            // TTL purge
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

            // Cap purge (oldest-first until under MaxStoredScreens)
            var max = Math.Max(0, _options.MaxStoredScreens);
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
                _logger.Information("EncryptedScreenStore: purged {Count} expired screen(s)", removed);
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

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SuavoAgent", "screens");
    }

    private void EnsureDirectoryWithAcl()
    {
        try
        {
            Directory.CreateDirectory(_directory);

            if (OperatingSystem.IsWindows())
            {
                // ACL lockdown — SYSTEM + Administrators + Interactive user.
                // Mirrors the ProgramData\SuavoAgent root lockdown in Core.
                var dirInfo = new DirectoryInfo(_directory);
                var security = dirInfo.GetAccessControl();
                security.SetAccessRuleProtection(true, false);

                security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    new System.Security.Principal.SecurityIdentifier(
                        System.Security.Principal.WellKnownSidType.LocalSystemSid, null),
                    System.Security.AccessControl.FileSystemRights.FullControl,
                    System.Security.AccessControl.InheritanceFlags.ContainerInherit |
                        System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                    System.Security.AccessControl.PropagationFlags.None,
                    System.Security.AccessControl.AccessControlType.Allow));

                security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    new System.Security.Principal.SecurityIdentifier(
                        System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null),
                    System.Security.AccessControl.FileSystemRights.FullControl,
                    System.Security.AccessControl.InheritanceFlags.ContainerInherit |
                        System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                    System.Security.AccessControl.PropagationFlags.None,
                    System.Security.AccessControl.AccessControlType.Allow));

                // Interactive user = the pharmacy operator at the console.
                // Helper runs as them, needs read+write to its own screens.
                security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    new System.Security.Principal.SecurityIdentifier(
                        System.Security.Principal.WellKnownSidType.InteractiveSid, null),
                    System.Security.AccessControl.FileSystemRights.Modify,
                    System.Security.AccessControl.InheritanceFlags.ContainerInherit |
                        System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                    System.Security.AccessControl.PropagationFlags.None,
                    System.Security.AccessControl.AccessControlType.Allow));

                dirInfo.SetAccessControl(security);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "EncryptedScreenStore: failed to set ACL on {Path}", _directory);
        }
    }

    private sealed class ScreenEnvelope
    {
        public string Id { get; set; } = "";
        public int Width { get; set; }
        public int Height { get; set; }
        public DateTimeOffset CapturedAt { get; set; }
        public string PngBase64 { get; set; } = "";
    }
}
