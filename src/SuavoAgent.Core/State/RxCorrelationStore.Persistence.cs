using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SuavoAgent.Core.State;

internal sealed partial class RxCorrelationStore
{
    private StoreDocument ReadAndPrune(DateTimeOffset now)
    {
        var document = ReadAndPrune(now, out var changed);
        if (changed) Write(document);
        return document;
    }

    private StoreDocument ReadAndPrune(DateTimeOffset now, out bool pruned)
    {
        var document = Read();
        var quarantined = false;
        foreach (var entry in document.Entries.Where(entry =>
                     entry.State == StoredCorrelationState.AwaitingCallback &&
                     (entry.AuthorizationExpiresAtUtc is null || entry.AuthorizationExpiresAtUtc <= now)))
        {
            entry.LastFailureCategory = "authorization_expired";
            Quarantine(entry, now);
            quarantined = true;
        }
        foreach (var entry in document.Entries.Where(entry =>
                     entry.State == StoredCorrelationState.CallbackAccepted &&
                     (entry.CallbackExpiresAtUtc is null || entry.CallbackExpiresAtUtc <= now)))
        {
            entry.LastFailureCategory = "callback_receipt_expired";
            Quarantine(entry, now);
            quarantined = true;
        }
        foreach (var entry in document.Entries.Where(entry =>
                     entry.State is StoredCorrelationState.CallbackAccepted or StoredCorrelationState.Completed &&
                     !string.IsNullOrEmpty(entry.ProtectedRx) &&
                     entry.ExpiresAtUtc <= now))
        {
            var expiredWriteback = false;
            foreach (var claim in entry.Writebacks.Where(claim =>
                         claim.State == StoredWritebackState.Registered))
            {
                claim.State = StoredWritebackState.Expired;
                claim.ExpiredAtUtc = now;
                expiredWriteback = true;
            }
            if (expiredWriteback)
                entry.LastFailureCategory = "writeback_authorization_expired";
            entry.ProtectedRx = null;
            entry.LookupMaterialPurged = true;
            entry.LastUpdatedAtUtc = now;
            entry.ExpiresAtUtc = now + _ttl;
            quarantined = true;
        }
        var before = document.Entries.Count;
        document.Entries.RemoveAll(e =>
            e.ExpiresAtUtc <= now &&
            (string.IsNullOrEmpty(e.ProtectedRx) ||
             (e.State == StoredCorrelationState.Observed &&
              e.CommandId is null &&
              e.Writebacks.Count == 0)));
        pruned = quarantined || before != document.Entries.Count;
        return document;
    }

    private StoreDocument Read()
    {
        if (!File.Exists(_filePath)) return new StoreDocument();
        EnsureProductionBoundary(fileMustExist: true);

        byte[] bytes;
        using (var stream = new FileStream(
                   _filePath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read,
                   4096,
                   FileOptions.SequentialScan))
        {
            if (stream.Length is <= 0 or > MaxStoreBytes)
                throw new InvalidDataException("Rx correlation store size is invalid.");
            bytes = ReadBounded(stream, MaxStoreBytes);
        }

        try
        {
            var document = JsonSerializer.Deserialize<StoreDocument>(bytes, JsonOptions)
                           ?? throw new InvalidDataException("Rx correlation store root is invalid.");
            ValidateDocument(document);
            return document;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Rx correlation store JSON is invalid.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private void Write(StoreDocument document)
    {
        document.SchemaVersion = SchemaVersion;
        ValidateDocument(document);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        if (bytes.Length is <= 0 or > MaxStoreBytes)
            throw new InvalidDataException("Rx correlation store exceeds its size limit.");

        var directory = Path.GetDirectoryName(_filePath)
                        ?? throw new InvalidOperationException("Rx correlation directory is unavailable.");
        Directory.CreateDirectory(directory);
        EnsureProductionBoundary(fileMustExist: false);
        var tempPath = _filePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            if (_requireProductionBoundary)
            {
                if (!OperatingSystem.IsWindows())
                    throw new PlatformNotSupportedException("The production Rx correlation store requires Windows.");
                ProductionAclBoundary.ValidateFile(tempPath);
            }
            File.Move(tempPath, _filePath, overwrite: true);
            if (_requireProductionBoundary)
            {
                if (!OperatingSystem.IsWindows())
                    throw new PlatformNotSupportedException("The production Rx correlation store requires Windows.");
                ProductionAclBoundary.ValidateFile(_filePath);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    private string Reveal(StoredCorrelation entry)
    {
        if (string.IsNullOrWhiteSpace(entry.ProtectedRx))
            throw new InvalidDataException("Rx correlation contains no protected lookup material.");

        byte[] protectedBytes;
        try { protectedBytes = Convert.FromBase64String(entry.ProtectedRx); }
        catch (FormatException ex) { throw new InvalidDataException("Rx correlation ciphertext is invalid.", ex); }
        if (protectedBytes.Length is <= 0 or > MaxProtectedRxBytes)
            throw new InvalidDataException("Rx correlation ciphertext size is invalid.");

        var key = new RxCorrelationKey(entry.PharmacyId, entry.AgentId, entry.RxHash, entry.EvidenceId);
        var entropy = BuildEntropy(
            key,
            entry.MachineFingerprint,
            entry.FillNumber,
            entry.ProtectionVersion,
            entry.SourceKind,
            entry.SourceBinding);
        byte[] clearBytes;
        try
        {
            clearBytes = _protector.Unprotect(protectedBytes, entropy);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidDataException("Rx correlation authentication failed.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            CryptographicOperations.ZeroMemory(entropy);
        }

        try
        {
            if (clearBytes.Length is <= 0 or > 128)
                throw new InvalidDataException("Rx correlation plaintext size is invalid.");
            var value = new UTF8Encoding(false, true).GetString(clearBytes);
            ValidateRawRx(value);
            return value;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clearBytes);
        }
    }

    private SensitiveRxBuffer RevealSensitive(StoredCorrelation entry)
    {
        if (string.IsNullOrWhiteSpace(entry.ProtectedRx))
            throw new InvalidDataException("Rx correlation contains no protected lookup material.");
        byte[] protectedBytes;
        try { protectedBytes = Convert.FromBase64String(entry.ProtectedRx); }
        catch (FormatException ex) { throw new InvalidDataException("Rx correlation ciphertext is invalid.", ex); }
        if (protectedBytes.Length is <= 0 or > MaxProtectedRxBytes)
            throw new InvalidDataException("Rx correlation ciphertext size is invalid.");
        var key = new RxCorrelationKey(entry.PharmacyId, entry.AgentId, entry.RxHash, entry.EvidenceId);
        var entropy = BuildEntropy(
            key, entry.MachineFingerprint, entry.FillNumber, entry.ProtectionVersion,
            entry.SourceKind, entry.SourceBinding);
        byte[] clearBytes;
        try { clearBytes = _protector.Unprotect(protectedBytes, entropy); }
        catch (CryptographicException ex)
        { throw new InvalidDataException("Rx correlation authentication failed.", ex); }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            CryptographicOperations.ZeroMemory(entropy);
        }

        try
        {
            if (clearBytes.Length is <= 0 or > 128)
                throw new InvalidDataException("Rx correlation plaintext size is invalid.");
            var encoding = new UTF8Encoding(false, true);
            var chars = new char[encoding.GetCharCount(clearBytes)];
            encoding.GetChars(clearBytes, chars);
            try
            {
                ValidateRawRx(chars);
                return new SensitiveRxBuffer(chars);
            }
            catch
            {
                Array.Clear(chars);
                throw;
            }
        }
        finally { CryptographicOperations.ZeroMemory(clearBytes); }
    }

}
