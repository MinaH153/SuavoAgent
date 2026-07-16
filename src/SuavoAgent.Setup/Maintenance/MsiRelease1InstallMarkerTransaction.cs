using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Setup.InstallerSupport;

namespace SuavoAgent.Setup.Maintenance;

/// <summary>
/// Durable rollback boundary for the Release 1 MSI marker. The active token and
/// journal must both match the current hidden MSI invocation before rollback can
/// mutate the marker. A committed tombstone is cleanup-only and never restorable.
/// </summary>
internal static class Release1MsiInstallMarkerTransaction
{
    internal const string JournalFileName = ".msi-release1-marker.rollback.json";
    internal const string LockFileName = ".msi-release1-marker.lock";
    internal const int JournalSchemaVersion = 2;
    private const int MaximumMarkerBytes = 64 * 1024;
    private const int MaximumJournalBytes = 128 * 1024;
    private static readonly AsyncLocal<int> ProofLockDepth = new();
    private static readonly ConcurrentDictionary<string, SemaphoreSlim>
        InProcessProofGates = new(
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);

    internal static bool IsProofLockHeldByCurrentContext => ProofLockDepth.Value > 0;

    internal static void PrepareAndWriteForInstalledHost(
        string originalDatabase,
        string productCode,
        string installDirectory,
        string invocationId)
    {
        var activation =
            FileMsiInstallerTransactionActivation.CreateForInstallDirectory(
                installDirectory);
        activation.RequireCurrent(invocationId);
        var proofDirectory = Release1MsiInstallMarkerStore.DefaultProofDirectory();
        using var proofLock = AcquireProofLock(proofDirectory);
        activation.RequireCurrent(invocationId);
        Prepare(proofDirectory, invocationId);
        try
        {
            Release1MsiInstallMarkerStore.WriteForInstalledHostUnderProofLock(
                installDirectory,
                originalDatabase,
                productCode,
                invocationId);
        }
        catch
        {
            // Restore immediately when marker creation fails. The queued MSI
            // rollback action retries only if this invocation remains active.
            try { Rollback(proofDirectory, invocationId); } catch { }
            throw;
        }
    }

    internal static void RollbackForInstalledHost(
        string installDirectory,
        string invocationId)
    {
        var activation =
            FileMsiInstallerTransactionActivation.CreateForInstallDirectory(
                installDirectory);
        activation.RequireCurrent(invocationId);
        var proofDirectory = Release1MsiInstallMarkerStore.DefaultProofDirectory();
        using var proofLock = AcquireProofLock(proofDirectory);
        activation.RequireCurrent(invocationId);
        Rollback(proofDirectory, invocationId);
    }

    internal static void CommitForInstalledHost(
        string installDirectory,
        string invocationId)
    {
        var activation =
            FileMsiInstallerTransactionActivation.CreateForInstallDirectory(
                installDirectory);
        activation.RequireCurrent(invocationId);
        var proofDirectory = Release1MsiInstallMarkerStore.DefaultProofDirectory();
        using var proofLock = AcquireProofLock(proofDirectory);
        activation.RequireCurrent(invocationId);
        Commit(proofDirectory, invocationId);
    }

    internal static IDisposable AcquireProofLock(string proofDirectory)
    {
        // Existing roots are verified without a recursive tree walk. Every
        // locked writer protects only its exact new file.
        var root = Directory.Exists(proofDirectory)
            ? Release1MsiInstallMarkerStore.RequireProtectedProofDirectory(
                proofDirectory)
            : Release1MsiInstallMarkerStore.CreateAndProtectProofDirectory(
                proofDirectory);
        var lockPath = Path.Combine(root, LockFileName);
        if (!File.Exists(lockPath))
        {
            try { WriteNewAtomic(lockPath, [1]); }
            catch (IOException) when (File.Exists(lockPath)) { }
            Release1MsiInstallMarkerStore.ProtectAndVerifyProofFile(
                root,
                lockPath,
                1);
        }
        Release1MsiInstallMarkerStore.VerifyProtectedProofObjects(
            root,
            [],
            [lockPath],
            1);
        var inProcessGate = InProcessProofGates.GetOrAdd(
            root,
            static _ => new SemaphoreSlim(1, 1));
        inProcessGate.Wait();
        FileStream? stream = null;
        try
        {
            stream = new FileStream(
                lockPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.ReadWrite,
                1,
                FileOptions.WriteThrough);
            if (stream.Length != 1)
            {
                stream.Dispose();
                throw new InvalidDataException(
                    "The MSI marker transaction lock is invalid.");
            }
            var regionLocked = false;
            if (OperatingSystem.IsWindows())
            {
                var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
                while (true)
                {
                    try
                    {
                        stream.Lock(0, 1);
                        regionLocked = true;
                        break;
                    }
                    catch (IOException) when (DateTimeOffset.UtcNow < deadline)
                    {
                        Thread.Sleep(100);
                    }
                    catch
                    {
                        stream.Dispose();
                        throw;
                    }
                }
            }
            return new ProofLockLease(stream, inProcessGate, regionLocked);
        }
        catch
        {
            stream?.Dispose();
            inProcessGate.Release();
            throw;
        }
    }

    internal static bool HasPendingJournal(string proofDirectory)
    {
        var root = Path.GetFullPath(proofDirectory);
        return File.Exists(Path.Combine(root, JournalFileName));
    }

    /// <summary>
    /// Refuses pending marker state from any invocation. A validated committed
    /// tombstone is cleanup-only and may be removed by the next transaction.
    /// Callers must hold the shared proof lock.
    /// </summary>
    internal static void RequireSettledForArmOrFinalization(string proofDirectory)
    {
        if (!IsProofLockHeldByCurrentContext)
            throw new InvalidOperationException(
                "The MSI proof lock is required to settle marker state.");
        var root = Release1MsiInstallMarkerStore.RequireProtectedProofDirectory(
            proofDirectory);
        var journalPath = Path.Combine(root, JournalFileName);
        if (!File.Exists(journalPath))
            return;

        var journal = ReadJournal(root, journalPath);
        if (journal.Phase != "committed")
            throw new InvalidDataException(
                "A pending MSI marker transaction blocks this invocation.");
        DeleteJournal(
            root,
            journalPath,
            journal.InvocationId,
            "committed");
    }

    internal static void Prepare(string proofDirectory, string invocationId)
    {
        ValidateInvocationId(invocationId);
        var root = Release1MsiInstallMarkerStore.RequireProtectedProofDirectory(
            proofDirectory);
        var journalPath = Path.Combine(root, JournalFileName);
        if (File.Exists(journalPath))
        {
            var existing = ReadJournal(root, journalPath);
            if (existing.Phase != "committed")
                throw new IOException("An MSI marker rollback journal already exists.");
            DeleteJournal(
                root,
                journalPath,
                existing.InvocationId,
                "committed");
        }

        var markerPath = Path.Combine(
            root,
            Release1ConvergenceContract.MsiInstallCommitMarkerFileName);
        byte[]? priorMarker = null;
        if (File.Exists(markerPath))
        {
            var parsed = Release1MsiInstallMarkerStore.Read(root);
            priorMarker = File.ReadAllBytes(markerPath);
            if (priorMarker.Length is <= 0 or > MaximumMarkerBytes ||
                !CryptographicOperations.FixedTimeEquals(
                    priorMarker,
                    Release1ConvergenceContract.CanonicalBytes(parsed)))
                throw new InvalidDataException("The prior MSI marker is not canonical.");
        }

        var document = new MarkerRollbackJournal(
            JournalSchemaVersion,
            invocationId,
            "pending",
            priorMarker is not null,
            priorMarker is null ? null : Convert.ToBase64String(priorMarker));
        var bytes = CanonicalBytes(document);
        WriteNewAtomic(journalPath, bytes);
        Release1MsiInstallMarkerStore.ProtectAndVerifyProofFile(
            root,
            journalPath,
            MaximumJournalBytes);
        if (!CryptographicOperations.FixedTimeEquals(bytes, File.ReadAllBytes(journalPath)))
            throw new IOException("The MSI marker rollback journal is not durable.");
    }

    internal static void Rollback(string proofDirectory, string invocationId)
    {
        ValidateInvocationId(invocationId);
        var root = Release1MsiInstallMarkerStore.RequireProtectedProofDirectory(
            proofDirectory);
        var journalPath = Path.Combine(root, JournalFileName);
        if (!File.Exists(journalPath))
            return;
        var journal = ReadJournal(root, journalPath);
        if (journal.Phase != "pending" ||
            !string.Equals(journal.InvocationId, invocationId, StringComparison.Ordinal))
            throw new InvalidDataException(
                "The MSI marker journal does not match this rollback invocation.");

        var markerPath = Path.Combine(
            root,
            Release1ConvergenceContract.MsiInstallCommitMarkerFileName);
        if (journal.HadPriorMarker)
        {
            var prior = Convert.FromBase64String(journal.PriorMarkerBase64!);
            WriteReplaceAtomic(markerPath, prior);
            Release1MsiInstallMarkerStore.ProtectAndVerifyProofFile(
                root,
                markerPath,
                MaximumMarkerBytes);
            if (!CryptographicOperations.FixedTimeEquals(
                    prior,
                    File.ReadAllBytes(markerPath)))
                throw new IOException("The prior MSI marker was not restored.");
            _ = Release1MsiInstallMarkerStore.Read(root);
        }
        else if (File.Exists(markerPath))
        {
            Release1MsiInstallMarkerStore.VerifyProtectedProofObjects(
                root,
                [],
                [markerPath],
                MaximumMarkerBytes);
            File.Delete(markerPath);
            if (File.Exists(markerPath))
                throw new IOException("The failed MSI marker remains present.");
        }

        DeleteJournal(root, journalPath, invocationId, "pending");
    }

    internal static void Commit(string proofDirectory, string invocationId)
    {
        MarkCommitted(proofDirectory, invocationId);
        var root = Release1MsiInstallMarkerStore.RequireProtectedProofDirectory(
            proofDirectory);
        var journalPath = Path.Combine(root, JournalFileName);
        if (!File.Exists(journalPath))
            return;
        // A crash from here leaves a committed tombstone. Rollback refuses it;
        // the next forward invocation may validate and delete it.
        DeleteJournal(root, journalPath, invocationId, "committed");
    }

    internal static void MarkCommitted(string proofDirectory, string invocationId)
    {
        ValidateInvocationId(invocationId);
        var root = Release1MsiInstallMarkerStore.RequireProtectedProofDirectory(
            proofDirectory);
        var journalPath = Path.Combine(root, JournalFileName);
        if (!File.Exists(journalPath))
            return;
        var journal = ReadJournal(root, journalPath);
        if (!string.Equals(journal.InvocationId, invocationId, StringComparison.Ordinal))
            throw new InvalidDataException(
                "The MSI marker journal does not match this commit invocation.");
        if (journal.Phase == "pending")
        {
            journal = journal with { Phase = "committed" };
            WriteReplaceAtomic(journalPath, CanonicalBytes(journal));
            Release1MsiInstallMarkerStore.ProtectAndVerifyProofFile(
                root,
                journalPath,
                MaximumJournalBytes);
            journal = ReadJournal(root, journalPath);
        }
        if (journal.Phase != "committed")
            throw new InvalidDataException("The MSI marker journal phase is invalid.");
    }

    private static void DeleteJournal(
        string root,
        string journalPath,
        string invocationId,
        string phase)
    {
        var current = ReadJournal(root, journalPath);
        if (!string.Equals(current.InvocationId, invocationId, StringComparison.Ordinal) ||
            !string.Equals(current.Phase, phase, StringComparison.Ordinal))
            throw new InvalidDataException("The MSI marker journal identity is invalid.");
        File.Delete(journalPath);
        if (File.Exists(journalPath))
            throw new IOException("The MSI marker rollback journal remains present.");
    }

    private static MarkerRollbackJournal ReadJournal(
        string root,
        string journalPath)
    {
        Release1MsiInstallMarkerStore.VerifyProtectedProofObjects(
            root,
            [],
            [journalPath],
            MaximumJournalBytes);
        var bytes = File.ReadAllBytes(journalPath);
        var journal = JsonSerializer.Deserialize<MarkerRollbackJournal>(bytes, JsonOptions)
            ?? throw new InvalidDataException("The MSI marker rollback journal is empty.");
        if (journal.SchemaVersion != JournalSchemaVersion ||
            !MsiInstallerInvocation.IsValidInvocationId(journal.InvocationId) ||
            journal.Phase is not ("pending" or "committed") ||
            journal.HadPriorMarker != (journal.PriorMarkerBase64 is not null))
            throw new InvalidDataException("The MSI marker rollback journal is invalid.");
        if (journal.HadPriorMarker)
        {
            byte[] prior;
            try { prior = Convert.FromBase64String(journal.PriorMarkerBase64!); }
            catch (FormatException exception)
            {
                throw new InvalidDataException(
                    "The MSI marker rollback journal is invalid.",
                    exception);
            }
            if (prior.Length is <= 0 or > MaximumMarkerBytes)
                throw new InvalidDataException("The MSI marker rollback journal is invalid.");
        }
        if (!CryptographicOperations.FixedTimeEquals(bytes, CanonicalBytes(journal)))
            throw new InvalidDataException("The MSI marker rollback journal is not canonical.");
        return journal;
    }

    private static byte[] CanonicalBytes(MarkerRollbackJournal journal)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(journal, JsonOptions);
        if (bytes.Length is <= 0 or > MaximumJournalBytes)
            throw new InvalidDataException("The MSI marker rollback journal is invalid.");
        return bytes;
    }

    private static void ValidateInvocationId(string invocationId)
    {
        if (!MsiInstallerInvocation.IsValidInvocationId(invocationId))
            throw new InvalidDataException("The MSI invocation identity is invalid.");
    }

    private static void WriteNewAtomic(string path, byte[] bytes)
    {
        var temporary = path + ".tmp";
        try
        {
            File.Delete(temporary);
            WriteBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: false);
        }
        finally
        {
            try { File.Delete(temporary); } catch { }
        }
    }

    private static void WriteReplaceAtomic(string path, byte[] bytes)
    {
        var temporary = path + ".tmp-replace";
        try
        {
            File.Delete(temporary);
            WriteBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); } catch { }
        }
    }

    private static void WriteBytes(string path, byte[] bytes)
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

    private static readonly JsonSerializerOptions JsonOptions = new(
        JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false,
    };

    private sealed record MarkerRollbackJournal(
        int SchemaVersion,
        string InvocationId,
        string Phase,
        bool HadPriorMarker,
        string? PriorMarkerBase64);

    private sealed class ProofLockLease : IDisposable
    {
        private FileStream? _stream;
        private SemaphoreSlim? _inProcessGate;
        private readonly bool _regionLocked;

        internal ProofLockLease(
            FileStream stream,
            SemaphoreSlim inProcessGate,
            bool regionLocked)
        {
            _stream = stream;
            _inProcessGate = inProcessGate;
            _regionLocked = regionLocked;
            ProofLockDepth.Value++;
        }

        public void Dispose()
        {
            var stream = Interlocked.Exchange(ref _stream, null);
            if (stream is null) return;
            var inProcessGate = Interlocked.Exchange(ref _inProcessGate, null);
            try
            {
                if (_regionLocked) stream.Unlock(0, 1);
            }
            finally
            {
                stream.Dispose();
                ProofLockDepth.Value--;
                inProcessGate!.Release();
            }
        }
    }
}
