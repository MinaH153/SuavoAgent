using System.Security.Cryptography;
using System.Text;

namespace SuavoAgent.Contracts.Security;

/// <summary>
/// Crash-safe, cross-process persistence for the signed live lease and its
/// signed anti-rollback floor. The high-water lease is never removed by a
/// revocation or expiry; therefore replacing only current.json with an older
/// still-live signed lease cannot reactivate observation after restart.
/// </summary>
public static class ObservationActivationStateStore
{
    private const string MutexName = @"Global\SuavoAgent.ObservationActivation.v1";
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);

    public static ObservationActivationInstallResult TryInstall(
        string currentPath,
        string highWaterPath,
        ObservationActivationState candidate,
        ObservationActivationIdentity identity,
        IReadOnlyDictionary<string, string> trustedKeys,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!TryAcquireCrossProcessLock(out var crossProcess) || crossProcess is null)
            return ObservationActivationInstallResult.Reject(
                ObservationActivationCodes.StateBusy);
        using (crossProcess)
        {

            var candidateSnapshot = ObservationActivationAuthority.Validate(
                candidate,
                identity,
                trustedKeys,
                now);
            if (!candidateSnapshot.ObservationEnabled ||
                candidateSnapshot.LeaseId is null ||
                candidateSnapshot.Nonce is null)
                return ObservationActivationInstallResult.Reject(candidateSnapshot.Code);

            var json = ObservationActivationAuthority.Serialize(candidate);
            var bytes = Encoding.UTF8.GetBytes(json);
            try
            {
                if (bytes.Length > ObservationActivationAuthority.MaximumStateBytes)
                    return ObservationActivationInstallResult.Reject(
                        ObservationActivationCodes.StateInvalid);
                var directory = Path.GetDirectoryName(currentPath);
                if (string.IsNullOrWhiteSpace(directory) ||
                    !string.Equals(
                        directory,
                        Path.GetDirectoryName(highWaterPath),
                        StringComparison.OrdinalIgnoreCase))
                    return ObservationActivationInstallResult.Reject(
                        ObservationActivationCodes.StateInvalid);
                Directory.CreateDirectory(directory);

                if (File.Exists(highWaterPath))
                {
                    var highWater = ObservationActivationAuthority.LoadHighWater(
                        highWaterPath,
                        identity,
                        trustedKeys);
                    if (!highWater.Valid)
                        return ObservationActivationInstallResult.Reject(highWater.Code);

                    if (candidateSnapshot.Generation < highWater.Generation)
                        return ObservationActivationInstallResult.Reject(
                            ObservationActivationCodes.ReplayDetected);
                    if (candidateSnapshot.Generation == highWater.Generation &&
                        (!string.Equals(
                             candidateSnapshot.LeaseId,
                             highWater.LeaseId,
                             StringComparison.Ordinal) ||
                         !string.Equals(
                             candidateSnapshot.Nonce,
                             highWater.Nonce,
                             StringComparison.Ordinal) ||
                         !ExactFileBytes(highWaterPath, bytes)))
                        return ObservationActivationInstallResult.Reject(
                            ObservationActivationCodes.ReplayDetected);
                }

                // Commit the signed high-water floor first. Readers use this same
                // mutex, so they observe either the previous pair or the new pair.
                // A crash between writes leaves the next reader dormant.
                WriteAtomic(highWaterPath, bytes);
                WriteAtomic(currentPath, bytes);
                return ObservationActivationInstallResult.Accepted(
                    candidateSnapshot.Generation,
                    candidateSnapshot.LeaseId);
            }
            catch (Exception ex) when (ex is
                IOException or UnauthorizedAccessException or ArgumentException)
            {
                TryDelete(currentPath);
                return ObservationActivationInstallResult.Reject(
                    ObservationActivationCodes.StatePersistenceFailed);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
    }

    private static bool ExactFileBytes(string path, byte[] candidate)
    {
        byte[] existing = Array.Empty<byte>();
        try
        {
            var info = new FileInfo(path);
            if (info.Length != candidate.Length ||
                info.Length is <= 0 or > ObservationActivationAuthority.MaximumStateBytes)
                return false;
            existing = File.ReadAllBytes(path);
            return existing.Length == candidate.Length &&
                   CryptographicOperations.FixedTimeEquals(existing, candidate);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(existing);
        }
    }

    public static bool RemoveCurrent(string currentPath)
    {
        if (!TryAcquireCrossProcessLock(out var crossProcess) || crossProcess is null)
            return false;
        using (crossProcess) return TryDelete(currentPath);
    }

    internal static bool TryAcquireCrossProcessLock(out IDisposable? releaser)
    {
        try
        {
            releaser = AcquireCrossProcessLock();
            return true;
        }
        catch (Exception ex) when (ex is
            TimeoutException or UnauthorizedAccessException or IOException)
        {
            releaser = null;
            return false;
        }
    }

    internal static IDisposable AcquireCrossProcessLock()
    {
        var mutex = new Mutex(initiallyOwned: false, MutexName);
        var acquired = false;
        try
        {
            try
            {
                acquired = mutex.WaitOne(LockTimeout);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (!acquired)
                throw new TimeoutException("Observation activation state is busy.");
            return new MutexReleaser(mutex);
        }
        catch
        {
            if (!acquired) mutex.Dispose();
            throw;
        }
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Activation state directory is missing.");
        var temporary = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            return !File.Exists(path);
        }
        catch
        {
            // Fail closed: the caller revalidates current state. A stale file
            // can never pass once its lease expires or high-water moves ahead.
            return false;
        }
    }

    private sealed class MutexReleaser(Mutex mutex) : IDisposable
    {
        private Mutex? _mutex = mutex;

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _mutex, null);
            if (current is null) return;
            current.ReleaseMutex();
            current.Dispose();
        }
    }
}
