using System.Text;
using SuavoAgent.Contracts.Maintenance;

namespace SuavoAgent.Setup.Maintenance;

/// <summary>
/// Fixed, strict coordination surface read by Watchdog. The immutable claim
/// lives in a staging-id directory; this pointer exposes only its bound paths,
/// replay identity, target, and a liveness heartbeat. No PHI is accepted.
/// </summary>
internal sealed class UpdateClaimPointerStore
{
    private readonly string _maintenanceRoot;
    private readonly string _pointerPath;
    private readonly string _completionPath;
    private readonly object _gate = new();

    public UpdateClaimPointerStore(string maintenanceRoot)
    {
        _maintenanceRoot = Path.GetFullPath(maintenanceRoot);
        _pointerPath = Path.Combine(_maintenanceRoot, UpdateActivationContract.ActiveClaimFileName);
        _completionPath = Path.Combine(_maintenanceRoot, UpdateActivationContract.CompletionFileName);
    }

    public string PointerPath => _pointerPath;
    public string CompletionPath => _completionPath;

    public UpdateActivationClaimPointer Begin(DurableUpdateClaim claim, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(claim);
        lock (_gate)
        {
            var pointer = new UpdateActivationClaimPointer(
                UpdateActivationContract.SchemaVersion,
                claim.Validated.ReplayId,
                claim.Validated.Request.StagingId,
                claim.Validated.Manifest.Version,
                claim.RequestPath,
                claim.PayloadDirectory,
                now.ToString("O"),
                now.ToString("O"));
            if (!UpdateActivationContract.ValidateClaimPointer(
                    pointer,
                    _maintenanceRoot,
                    now,
                    out var code))
                throw new InvalidDataException("Active claim pointer rejected: " + code);

            var existing = TryReadPointer(now);
            if (existing is not null &&
                !string.Equals(existing.ReplayId, pointer.ReplayId, StringComparison.Ordinal))
                throw new InvalidOperationException("Another update claim is active.");

            // A terminal receipt from the previous update may be replaced only
            // when there is no different active claim.
            if (File.Exists(_completionPath))
            {
                if (existing is not null)
                {
                    var completion = TryReadCompletion(existing, now);
                    if (completion is null)
                        throw new InvalidDataException("Existing completion receipt is invalid.");
                    throw new InvalidOperationException(
                        "The active update already has a terminal completion receipt.");
                }
                var rawCompletion = ReadCompletionWithoutPointer();
                if (rawCompletion is not null &&
                    string.Equals(rawCompletion.ReplayId, pointer.ReplayId, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "This update replay already has a terminal completion receipt.");
                File.Delete(_completionPath);
            }
            WriteAtomic(_pointerPath, UpdateActivationContract.Serialize(pointer));
            return pointer;
        }
    }

    public UpdateActivationClaimPointer Heartbeat(
        UpdateActivationClaimPointer expected,
        DateTimeOffset now)
    {
        lock (_gate)
        {
            var current = TryReadPointer(now)
                          ?? throw new InvalidDataException("Active claim pointer is missing.");
            if (!string.Equals(current.ReplayId, expected.ReplayId, StringComparison.Ordinal))
                throw new InvalidDataException("Active claim pointer replay changed.");
            var next = current with { LastHeartbeatAtUtc = now.ToString("O") };
            if (!UpdateActivationContract.ValidateClaimPointer(
                    next,
                    _maintenanceRoot,
                    now,
                    out var code))
                throw new InvalidDataException("Active claim heartbeat rejected: " + code);
            WriteAtomic(_pointerPath, UpdateActivationContract.Serialize(next));
            return next;
        }
    }

    public void Complete(
        UpdateActivationClaimPointer pointer,
        string outcome,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt)
    {
        lock (_gate)
        {
            var current = TryReadPointer(completedAt)
                          ?? throw new InvalidDataException("Active claim pointer is missing at completion.");
            if (!string.Equals(current.ReplayId, pointer.ReplayId, StringComparison.Ordinal))
                throw new InvalidDataException("Completion does not match the active claim.");
            var completion = new UpdateActivationCompletion(
                UpdateActivationContract.SchemaVersion,
                current.ReplayId,
                current.StagingId,
                current.TargetVersion,
                outcome,
                startedAt.ToString("O"),
                completedAt.ToString("O"));
            if (!UpdateActivationContract.ValidateCompletion(
                    completion,
                    current,
                    completedAt,
                    out var code))
                throw new InvalidDataException("Completion receipt rejected: " + code);
            WriteAtomic(_completionPath, UpdateActivationContract.Serialize(completion));
            // Completion is durable before the active pointer disappears.
            File.Delete(_pointerPath);
        }
    }

    public UpdateActivationClaimPointer? TryReadPointer(DateTimeOffset now)
    {
        if (!File.Exists(_pointerPath)) return null;
        var json = ReadBounded(_pointerPath, UpdateActivationContract.MaxClaimPointerBytes);
        if (!UpdateActivationContract.TryDeserializeClaimPointer(json, out var pointer, out _) ||
            !UpdateActivationContract.ValidateClaimPointer(
                pointer!,
                _maintenanceRoot,
                now,
                out _))
            return null;
        return pointer;
    }

    private UpdateActivationCompletion? TryReadCompletion(
        UpdateActivationClaimPointer pointer,
        DateTimeOffset now)
    {
        if (!File.Exists(_completionPath)) return null;
        var json = ReadBounded(_completionPath, UpdateActivationContract.MaxCompletionBytes);
        if (!UpdateActivationContract.TryDeserializeCompletion(json, out var completion, out _) ||
            !UpdateActivationContract.ValidateCompletion(completion!, pointer, now, out _))
            return null;
        return completion;
    }

    private UpdateActivationCompletion? ReadCompletionWithoutPointer()
    {
        try
        {
            var json = ReadBounded(_completionPath, UpdateActivationContract.MaxCompletionBytes);
            return UpdateActivationContract.TryDeserializeCompletion(
                json,
                out var completion,
                out _)
                ? completion
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string ReadBounded(string path, int maximumBytes)
        => BoundedFile.ReadUtf8(path, maximumBytes);

    private static void WriteAtomic(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, content, new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }
}
