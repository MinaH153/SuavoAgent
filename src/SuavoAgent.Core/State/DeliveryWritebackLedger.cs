using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SuavoAgent.Contracts.Maintenance;

namespace SuavoAgent.Core.State;

/// <summary>
/// DPAPI-protected, atomically replaced command ledger. It contains no raw Rx
/// or receipt PHI, but protection prevents a local file edit from changing a
/// signed command's execution/result state.
/// </summary>
internal sealed class DeliveryWritebackLedger : IDeliveryWritebackLedger
{
    private const int SchemaVersion = 1;
    private const int MaxEntries = 4096;
    private const int MaxFileBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan AckedRetention = TimeSpan.FromDays(7);
    private static readonly byte[] Entropy = SHA256.HashData(
        "SuavoAgent.DeliveryWritebackLedger.v1"u8.ToArray());
    private static readonly Regex OffsetTimestamp = new(
        @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly ConcurrentDictionary<string, object> PathGates =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8,
        WriteIndented = false,
    };

    private readonly string _path;
    private readonly IRxCorrelationProtector _protector;
    private readonly TimeProvider _time;
    private readonly bool _productionBoundary;
    private readonly IReadOnlyDictionary<string, string> _trustedReceiptKeys;
    private readonly object _gate;

    internal DeliveryWritebackLedger(
        string path,
        IRxCorrelationProtector protector,
        TimeProvider? timeProvider = null,
        bool requireProductionBoundary = false,
        IReadOnlyDictionary<string, string>? trustedReceiptKeys = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(protector);
        _path = Path.GetFullPath(path);
        _protector = protector;
        _time = timeProvider ?? TimeProvider.System;
        _productionBoundary = requireProductionBoundary;
        _trustedReceiptKeys = trustedReceiptKeys ?? RemoteCommandTrust.CreateProductionKeyRegistry();
        _gate = PathGates.GetOrAdd(_path, static _ => new object());
    }

    [SupportedOSPlatform("windows")]
    internal static DeliveryWritebackLedger CreateProduction()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The production delivery writeback ledger requires Windows DPAPI.");
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SuavoAgent",
            "delivery-writebacks.v1.bin");
        return new DeliveryWritebackLedger(
            path,
            new DpapiRxCorrelationProtector(),
            requireProductionBoundary: true);
    }

    public DeliveryWritebackLedgerRegistrationResult Register(AgentDeliveryWritebackCommand command)
    {
        ValidateCommand(command);
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            var document = ReadAndPrune(now);
            var commandReplay = document.Entries.SingleOrDefault(entry =>
                string.Equals(entry.CommandId, command.CommandId, StringComparison.Ordinal));
            if (commandReplay is not null)
            {
                return ExactCommand(commandReplay, command)
                    ? new DeliveryWritebackLedgerRegistrationResult(
                        DeliveryWritebackLedgerRegistrationCode.Idempotent,
                        ToItem(commandReplay))
                    : new DeliveryWritebackLedgerRegistrationResult(
                        DeliveryWritebackLedgerRegistrationCode.CommandConflict,
                        null);
            }

            if (document.Entries.Any(entry =>
                    string.Equals(entry.WritebackId, command.WritebackId, StringComparison.Ordinal) ||
                    (string.Equals(entry.OrderId, command.OrderId, StringComparison.Ordinal) &&
                     string.Equals(entry.Transition, command.Transition, StringComparison.Ordinal))))
            {
                return new DeliveryWritebackLedgerRegistrationResult(
                    DeliveryWritebackLedgerRegistrationCode.WritebackConflict,
                    null);
            }

            MakeRoom(document);
            if (document.Entries.Count >= MaxEntries)
                return new DeliveryWritebackLedgerRegistrationResult(
                    DeliveryWritebackLedgerRegistrationCode.Full,
                    null);

            var entry = StoredEntry.FromCommand(command, now);
            document.Entries.Add(entry);
            Write(document);
            return new DeliveryWritebackLedgerRegistrationResult(
                DeliveryWritebackLedgerRegistrationCode.Registered,
                ToItem(entry));
        }
    }

    public IReadOnlyList<DeliveryWritebackLedgerItem> GetDue(
        string pharmacyId,
        int maxCount,
        DateTimeOffset now)
    {
        ValidateUuid(pharmacyId, nameof(pharmacyId));
        if (maxCount is <= 0 or > 32) throw new ArgumentOutOfRangeException(nameof(maxCount));
        lock (_gate)
        {
            var document = ReadAndPrune(now, out var pruned);
            if (pruned) Write(document);
            return document.Entries
                .Where(entry =>
                    entry.State != DeliveryWritebackLedgerState.Acked &&
                    string.Equals(entry.PharmacyId, pharmacyId, StringComparison.Ordinal) &&
                    (entry.NextRetryAt is null || entry.NextRetryAt <= now))
                .OrderBy(entry => entry.UpdatedAt)
                .Take(maxCount)
                .Select(ToItem)
                .ToArray();
        }
    }

    public DeliveryWritebackLedgerItem? Get(string commandId)
    {
        ValidateUuid(commandId, nameof(commandId));
        lock (_gate)
        {
            var document = ReadAndPrune(_time.GetUtcNow(), out var pruned);
            if (pruned) Write(document);
            var entry = document.Entries.SingleOrDefault(item =>
                string.Equals(item.CommandId, commandId, StringComparison.Ordinal));
            return entry is null ? null : ToItem(entry);
        }
    }

    public DeliveryWritebackLedgerItem MarkExecuting(string commandId) =>
        Mutate(commandId, entry =>
        {
            if (entry.State is not (DeliveryWritebackLedgerState.Registered or
                DeliveryWritebackLedgerState.Executing))
                throw new InvalidOperationException("Writeback is not eligible for execution.");
            if (!entry.CorrelationBound)
                throw new InvalidOperationException("Writeback has no exact local correlation binding.");
            entry.State = DeliveryWritebackLedgerState.Executing;
            entry.ExecutionAttempts = checked(entry.ExecutionAttempts + 1);
            entry.NextRetryAt = null;
            entry.LastErrorCode = null;
        });

    public DeliveryWritebackLedgerItem MarkCorrelationBound(string commandId) =>
        Mutate(commandId, entry => entry.CorrelationBound = true);

    public DeliveryWritebackLedgerItem RecordResult(
        string commandId,
        DeliveryWritebackResultCode resultCode) =>
        Mutate(commandId, entry =>
        {
            if (entry.ResultCode is { } existing)
            {
                if (existing != resultCode)
                    throw new InvalidDataException("Writeback terminal result conflicts with durable state.");
                return;
            }
            if (entry.State is not (DeliveryWritebackLedgerState.Registered or
                DeliveryWritebackLedgerState.Executing))
                throw new InvalidOperationException("Writeback cannot accept an execution result in its current state.");
            entry.ResultCode = resultCode;
            entry.State = DeliveryWritebackLedgerState.ResultPendingCallback;
            entry.NextRetryAt = null;
            entry.LastErrorCode = null;
        });

    public DeliveryWritebackLedgerItem Defer(
        string commandId,
        string errorCode,
        DateTimeOffset nextRetryAt,
        bool callbackAttempt) =>
        Mutate(commandId, entry =>
        {
            ValidateErrorCode(errorCode);
            if (entry.State == DeliveryWritebackLedgerState.Acked)
                throw new InvalidOperationException("An acknowledged writeback cannot be deferred.");
            if (callbackAttempt)
                entry.CallbackAttempts = checked(entry.CallbackAttempts + 1);
            entry.NextRetryAt = nextRetryAt;
            entry.LastErrorCode = errorCode;
        });

    public DeliveryWritebackLedgerItem MarkReceiptVerified(
        string commandId,
        DeliveryWritebackCallbackReceipt receipt) =>
        Mutate(commandId, entry =>
        {
            ValidateReceipt(entry, receipt);
            if (entry.State is DeliveryWritebackLedgerState.ReceiptVerified or
                DeliveryWritebackLedgerState.Acked)
            {
                if (entry.Receipt is null || ToReceipt(entry) != receipt)
                    throw new InvalidDataException("Authenticated receipt conflicts with durable state.");
                return;
            }
            if (entry.State != DeliveryWritebackLedgerState.ResultPendingCallback)
                throw new InvalidOperationException("Writeback is not awaiting a callback receipt.");
            entry.Receipt = StoredReceipt.FromReceipt(receipt);
            entry.State = DeliveryWritebackLedgerState.ReceiptVerified;
            entry.NextRetryAt = null;
            entry.LastErrorCode = null;
        });

    public DeliveryWritebackLedgerItem MarkAcked(string commandId) =>
        Mutate(commandId, entry =>
        {
            if (entry.State == DeliveryWritebackLedgerState.Acked) return;
            if (entry.State != DeliveryWritebackLedgerState.ReceiptVerified || entry.Receipt is null)
                throw new InvalidOperationException("Writeback cannot be ACKed before an authenticated receipt.");
            entry.State = DeliveryWritebackLedgerState.Acked;
            entry.NextRetryAt = null;
            entry.LastErrorCode = null;
        });

    private DeliveryWritebackLedgerItem Mutate(string commandId, Action<StoredEntry> mutation)
    {
        ValidateUuid(commandId, nameof(commandId));
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            var document = ReadAndPrune(now);
            var entry = document.Entries.SingleOrDefault(item =>
                string.Equals(item.CommandId, commandId, StringComparison.Ordinal))
                ?? throw new InvalidOperationException("Delivery writeback command is not registered.");
            mutation(entry);
            entry.UpdatedAt = now;
            ValidateEntry(entry);
            Write(document);
            return ToItem(entry);
        }
    }

    private StoreDocument ReadAndPrune(DateTimeOffset now) => ReadAndPrune(now, out _);

    private StoreDocument ReadAndPrune(DateTimeOffset now, out bool pruned)
    {
        var document = Read();
        var before = document.Entries.Count;
        document.Entries.RemoveAll(entry =>
            entry.State == DeliveryWritebackLedgerState.Acked &&
            now - entry.UpdatedAt >= AckedRetention);
        pruned = document.Entries.Count != before;
        return document;
    }

    private StoreDocument Read()
    {
        if (!File.Exists(_path)) return new StoreDocument();
        EnsureProductionBoundary(fileMustExist: true);
        byte[] protectedBytes;
        using (var stream = new FileStream(
                   _path, FileMode.Open, FileAccess.Read, FileShare.Read,
                   4096, FileOptions.SequentialScan))
        {
            if (stream.Length is <= 0 or > MaxFileBytes)
                throw new InvalidDataException("Delivery writeback ledger size is invalid.");
            protectedBytes = ReadBounded(stream, MaxFileBytes);
        }

        byte[] clearBytes;
        try
        {
            clearBytes = _protector.Unprotect(protectedBytes, Entropy);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidDataException("Delivery writeback ledger authentication failed.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
        }

        try
        {
            var document = JsonSerializer.Deserialize<StoreDocument>(clearBytes, JsonOptions)
                           ?? throw new InvalidDataException("Delivery writeback ledger root is invalid.");
            ValidateDocument(document);
            return document;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Delivery writeback ledger JSON is invalid.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clearBytes);
        }
    }

    private void Write(StoreDocument document)
    {
        document.SchemaVersion = SchemaVersion;
        ValidateDocument(document);
        var clearBytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        byte[] protectedBytes;
        try
        {
            protectedBytes = _protector.Protect(clearBytes, Entropy);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clearBytes);
        }
        if (protectedBytes.Length is <= 0 or > MaxFileBytes)
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            throw new InvalidDataException("Delivery writeback ledger exceeds its size limit.");
        }

        var directory = Path.GetDirectoryName(_path)
                        ?? throw new InvalidOperationException("Delivery writeback ledger directory is unavailable.");
        Directory.CreateDirectory(directory);
        EnsureProductionBoundary(fileMustExist: false);
        var temp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(
                       temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                stream.Write(protectedBytes);
                stream.Flush(flushToDisk: true);
            }
            if (_productionBoundary)
            {
                if (!OperatingSystem.IsWindows())
                    throw new PlatformNotSupportedException("The production delivery writeback ledger requires Windows.");
                ProductionAclBoundary.ValidateFile(temp);
            }
            File.Move(temp, _path, overwrite: true);
            if (_productionBoundary)
            {
                if (!OperatingSystem.IsWindows())
                    throw new PlatformNotSupportedException("The production delivery writeback ledger requires Windows.");
                ProductionAclBoundary.ValidateFile(_path);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    private void MakeRoom(StoreDocument document)
    {
        if (document.Entries.Count < MaxEntries) return;
        var evictable = document.Entries
            .Where(entry => entry.State == DeliveryWritebackLedgerState.Acked)
            .OrderBy(entry => entry.UpdatedAt)
            .FirstOrDefault();
        if (evictable is not null) document.Entries.Remove(evictable);
    }

    private static bool ExactCommand(StoredEntry entry, AgentDeliveryWritebackCommand command) =>
        ToCommand(entry) == command;

    private static DeliveryWritebackLedgerItem ToItem(StoredEntry entry) => new(
        ToCommand(entry),
        entry.State,
        entry.ResultCode,
        entry.CorrelationBound,
        entry.ExecutionAttempts,
        entry.CallbackAttempts,
        entry.NextRetryAt,
        entry.LastErrorCode,
        entry.Receipt is null ? null : ToReceipt(entry),
        entry.CreatedAt,
        entry.UpdatedAt);

    private static AgentDeliveryWritebackCommand ToCommand(StoredEntry entry) => new(
        entry.SchemaVersion,
        entry.WritebackId,
        entry.CandidateId,
        entry.RxHash,
        entry.EvidenceId,
        entry.PharmacyId,
        entry.OrderId,
        entry.InboxItemId,
        entry.PmsReferenceId,
        entry.ProofRecordId,
        entry.ProofDigest,
        entry.Transition,
        entry.TransitionAt,
        entry.CommandId);

    private static DeliveryWritebackCallbackReceipt ToReceipt(StoredEntry entry)
    {
        var receipt = entry.Receipt
                      ?? throw new InvalidDataException("Delivery writeback receipt is missing.");
        return new DeliveryWritebackCallbackReceipt(
            entry.WritebackId,
            entry.CommandId,
            entry.PharmacyId,
            entry.OrderId,
            entry.CandidateId,
            entry.PmsReferenceId,
            entry.ProofRecordId,
            entry.ProofDigest,
            entry.Transition,
            receipt.Status,
            entry.ResultCode ?? throw new InvalidDataException("Delivery writeback result is missing."),
            receipt.CompletedAt,
            receipt.Idempotent,
            new DeliveryWritebackSignedProof(
                receipt.KeyId,
                receipt.SignatureBase64,
                receipt.CanonicalBodySha256,
                receipt.CanonicalBodyJson));
    }

    private void ValidateDocument(StoreDocument document)
    {
        if (document.SchemaVersion != SchemaVersion || document.Entries is null ||
            document.Entries.Count > MaxEntries)
            throw new InvalidDataException("Delivery writeback ledger schema is invalid.");
        var commandIds = new HashSet<string>(StringComparer.Ordinal);
        var writebackIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in document.Entries)
        {
            ValidateEntry(entry);
            if (!commandIds.Add(entry.CommandId) || !writebackIds.Add(entry.WritebackId))
                throw new InvalidDataException("Delivery writeback ledger contains duplicate identities.");
        }
    }

    private void ValidateEntry(StoredEntry entry)
    {
        ValidateCommand(ToCommand(entry));
        if (entry.CreatedAt == default || entry.UpdatedAt < entry.CreatedAt ||
            entry.ExecutionAttempts < 0 || entry.CallbackAttempts < 0)
            throw new InvalidDataException("Delivery writeback ledger timing/counters are invalid.");
        if (!Enum.IsDefined(entry.State) ||
            entry.ResultCode is { } storedResult && !Enum.IsDefined(storedResult))
            throw new InvalidDataException("Delivery writeback ledger enum value is invalid.");
        if (entry.LastErrorCode is not null) ValidateErrorCode(entry.LastErrorCode);

        if (entry.State is DeliveryWritebackLedgerState.Registered or DeliveryWritebackLedgerState.Executing)
        {
            if (entry.ResultCode is not null || entry.Receipt is not null)
                throw new InvalidDataException("Pre-result writeback state contains terminal data.");
        }
        else if (entry.State == DeliveryWritebackLedgerState.ResultPendingCallback)
        {
            if (entry.ResultCode is null || entry.Receipt is not null)
                throw new InvalidDataException("Callback-pending writeback state is inconsistent.");
        }
        else
        {
            if (entry.ResultCode is null || entry.Receipt is null)
                throw new InvalidDataException("Receipt/ACK writeback state is inconsistent.");
            ValidateReceipt(entry, ToReceipt(entry));
        }
    }

    private static void ValidateCommand(AgentDeliveryWritebackCommand command)
    {
        if (command.SchemaVersion != 2) throw new ArgumentException("Unsupported schema version.");
        ValidateUuid(command.WritebackId, nameof(command.WritebackId));
        ValidateUuid(command.CandidateId, nameof(command.CandidateId));
        ValidateUuid(command.PharmacyId, nameof(command.PharmacyId));
        ValidateUuid(command.OrderId, nameof(command.OrderId));
        ValidateUuid(command.InboxItemId, nameof(command.InboxItemId));
        ValidateUuid(command.PmsReferenceId, nameof(command.PmsReferenceId));
        ValidateUuid(command.CommandId, nameof(command.CommandId));
        if (command.RxHash.Length != 64 ||
            command.RxHash.Any(ch => ch is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("Rx hash is invalid.");
        var evidencePrefix = $"rxh-{command.RxHash[..16]}-";
        if (!command.EvidenceId.StartsWith(evidencePrefix, StringComparison.Ordinal) ||
            command.EvidenceId.Length < evidencePrefix.Length + 10 ||
            command.EvidenceId.Length > evidencePrefix.Length + 13 ||
            command.EvidenceId[evidencePrefix.Length..].Any(ch => ch is < '0' or > '9'))
            throw new ArgumentException("Evidence binding is invalid.");
        if (command.Transition is not ("pickup" or "complete") ||
            !OffsetTimestamp.IsMatch(command.TransitionAt) ||
            !DateTimeOffset.TryParse(
                command.TransitionAt,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out _))
            throw new ArgumentException("Transition is invalid.");
        if (command.Transition == "complete")
        {
            ValidateUuid(command.ProofRecordId ?? "", nameof(command.ProofRecordId));
            if (command.ProofDigest is not { Length: 64 } ||
                command.ProofDigest.Any(ch => ch is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
                throw new ArgumentException("Completion proof digest is invalid.");
        }
        else if (command.ProofRecordId is not null || command.ProofDigest is not null)
        {
            throw new ArgumentException("Pickup writeback cannot carry completion proof.");
        }
    }

    private void ValidateReceipt(StoredEntry entry, DeliveryWritebackCallbackReceipt receipt)
    {
        var expectedStatus = entry.ResultCode switch
        {
            DeliveryWritebackResultCode.Success or DeliveryWritebackResultCode.AlreadyAtTarget => "succeeded",
            _ => "needs_attention",
        };
        if (receipt.WritebackId != entry.WritebackId || receipt.CommandId != entry.CommandId ||
            receipt.PharmacyId != entry.PharmacyId || receipt.OrderId != entry.OrderId ||
            receipt.CandidateId != entry.CandidateId || receipt.Transition != entry.Transition ||
            receipt.ResultCode != entry.ResultCode || receipt.Status != expectedStatus ||
            receipt.CompletedAt == default ||
            !receipt.Proof.Verify(receipt, _trustedReceiptKeys))
            throw new InvalidDataException("Authenticated delivery writeback receipt identity is invalid.");
    }

    private static void ValidateUuid(string value, string name)
    {
        if (value.Length != 36 || !Guid.TryParseExact(value, "D", out var parsed) ||
            parsed.ToString("D") != value)
            throw new ArgumentException("Expected a canonical lowercase UUID.", name);
    }

    private static void ValidateErrorCode(string code)
    {
        if (code.Length is < 1 or > 80 ||
            code.Any(ch => ch is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '_'))
            throw new ArgumentException("Writeback error code is invalid.", nameof(code));
    }

    private void EnsureProductionBoundary(bool fileMustExist)
    {
        if (!_productionBoundary) return;
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The production delivery writeback ledger requires Windows.");
        ProductionAclBoundary.ValidatePath(
            _path,
            "delivery-writebacks.v1.bin",
            fileMustExist);
    }

    private static byte[] ReadBounded(Stream stream, int maxBytes)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        var total = 0;
        while (true)
        {
            var read = stream.Read(buffer, 0, Math.Min(buffer.Length, maxBytes + 1 - total));
            if (read == 0) break;
            total += read;
            if (total > maxBytes) throw new InvalidDataException("Delivery writeback ledger is too large.");
            memory.Write(buffer, 0, read);
        }
        return memory.ToArray();
    }

    private sealed class StoreDocument
    {
        public int SchemaVersion { get; set; } = DeliveryWritebackLedger.SchemaVersion;
        public List<StoredEntry> Entries { get; set; } = [];
    }

    private sealed class StoredEntry
    {
        public int SchemaVersion { get; set; }
        public string WritebackId { get; set; } = "";
        public string CandidateId { get; set; } = "";
        public string RxHash { get; set; } = "";
        public string EvidenceId { get; set; } = "";
        public string PharmacyId { get; set; } = "";
        public string OrderId { get; set; } = "";
        public string InboxItemId { get; set; } = "";
        public string PmsReferenceId { get; set; } = "";
        public string? ProofRecordId { get; set; }
        public string? ProofDigest { get; set; }
        public string Transition { get; set; } = "";
        public string TransitionAt { get; set; } = "";
        public string CommandId { get; set; } = "";
        [JsonConverter(typeof(JsonStringEnumConverter<DeliveryWritebackLedgerState>))]
        public DeliveryWritebackLedgerState State { get; set; }
        [JsonConverter(typeof(JsonStringEnumConverter<DeliveryWritebackResultCode>))]
        public DeliveryWritebackResultCode? ResultCode { get; set; }
        public bool CorrelationBound { get; set; }
        public int ExecutionAttempts { get; set; }
        public int CallbackAttempts { get; set; }
        public DateTimeOffset? NextRetryAt { get; set; }
        public string? LastErrorCode { get; set; }
        public StoredReceipt? Receipt { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }

        internal static StoredEntry FromCommand(AgentDeliveryWritebackCommand command, DateTimeOffset now) => new()
        {
            SchemaVersion = command.SchemaVersion,
            WritebackId = command.WritebackId,
            CandidateId = command.CandidateId,
            RxHash = command.RxHash,
            EvidenceId = command.EvidenceId,
            PharmacyId = command.PharmacyId,
            OrderId = command.OrderId,
            InboxItemId = command.InboxItemId,
            PmsReferenceId = command.PmsReferenceId,
            ProofRecordId = command.ProofRecordId,
            ProofDigest = command.ProofDigest,
            Transition = command.Transition,
            TransitionAt = command.TransitionAt,
            CommandId = command.CommandId,
            State = DeliveryWritebackLedgerState.Registered,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private sealed class StoredReceipt
    {
        public string Status { get; set; } = "";
        public DateTimeOffset CompletedAt { get; set; }
        public bool Idempotent { get; set; }
        public string KeyId { get; set; } = "";
        public string SignatureBase64 { get; set; } = "";
        public string CanonicalBodySha256 { get; set; } = "";
        public string CanonicalBodyJson { get; set; } = "";

        internal static StoredReceipt FromReceipt(DeliveryWritebackCallbackReceipt receipt) => new()
        {
            Status = receipt.Status,
            CompletedAt = receipt.CompletedAt,
            Idempotent = receipt.Idempotent,
            KeyId = receipt.Proof.KeyId,
            SignatureBase64 = receipt.Proof.SignatureBase64,
            CanonicalBodySha256 = receipt.Proof.CanonicalBodySha256,
            CanonicalBodyJson = receipt.Proof.CanonicalBodyJson,
        };
    }
}
