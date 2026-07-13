using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SuavoAgent.Contracts.Adapters;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Learning;

internal interface IActivePmsAdapterRegistry
{
    AdapterActivationResult ActivateApproved(string sessionId);
    ActivePmsAdapterLease? TryAcquire(DateTimeOffset now);
    void ReportHealthy(ActivePmsAdapterBinding binding, DateTimeOffset now);
    void ReportUnhealthy(ActivePmsAdapterBinding binding, DateTimeOffset now, string errorCategory);
    ActivePmsAdapterStatus Snapshot(DateTimeOffset now);
    ActivePmsAdapterBinding? CurrentBinding() => null;
}

internal enum AdapterActivationOutcome
{
    Activated,
    AlreadyActive,
    Rejected,
    Failed,
}

internal sealed record AdapterActivationResult(
    AdapterActivationOutcome Outcome,
    string Reason,
    ActivePmsAdapterBinding? Binding = null)
{
    internal bool IsActive => Outcome is AdapterActivationOutcome.Activated or AdapterActivationOutcome.AlreadyActive;
}

internal sealed record ActivePmsAdapterBinding(
    string PharmacyId,
    string SessionId,
    string TemplateDigest,
    string ModelDigest,
    string ApprovedBy,
    DateTimeOffset ApprovedAt);

internal sealed record ActivePmsAdapterStatus(
    bool HasActiveAdapter,
    string? SessionId,
    string? TemplateDigestPrefix,
    int ConsecutiveHealthFailures,
    DateTimeOffset? RetryAfter,
    DateTimeOffset? LastHealthyAt)
{
    internal bool IsAvailable(DateTimeOffset now) =>
        HasActiveAdapter && (RetryAfter is null || now >= RetryAfter.Value);
}

/// <summary>
/// Reference-counted lease prevents a learned adapter from being disposed while
/// a detection query is in flight.
/// </summary>
internal sealed class ActivePmsAdapterLease : IDisposable
{
    private Action? _release;

    internal ActivePmsAdapterLease(
        ILocalPmsAdapter adapter,
        ActivePmsAdapterBinding binding,
        Action release)
    {
        Adapter = adapter;
        Binding = binding;
        _release = release;
    }

    internal ILocalPmsAdapter Adapter { get; }
    internal ActivePmsAdapterBinding Binding { get; }

    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}

/// <summary>
/// Owns the one learned PMS adapter that is eligible for live reads. Activation
/// is fail-closed unless the local SQLCipher state contains a human approval for
/// the exact frozen POM and exact session-bound adapter-template digest.
/// </summary>
internal sealed class ActivePmsAdapterRegistry : IActivePmsAdapterRegistry, IDisposable
{
    private static readonly TimeSpan MaxHealthBackoff = TimeSpan.FromMinutes(5);
    private readonly object _gate = new();
    private readonly AgentStateDb _db;
    private readonly AgentOptions _options;
    private readonly ILogger<ActivePmsAdapterRegistry> _logger;
    private readonly Func<LearnedPmsAdapterTemplate, ILocalPmsAdapter> _adapterFactory;
    private readonly Func<LearnedPmsAdapterTemplate, string?> _liveSourceValidator;
    private RegistryEntry? _active;
    private bool _disposed;

    public ActivePmsAdapterRegistry(
        AgentStateDb db,
        IOptions<AgentOptions> options,
        ILoggerFactory loggerFactory,
        ILogger<ActivePmsAdapterRegistry> logger)
        : this(
            db,
            options.Value,
            logger,
            template => AdapterGenerator.Generate(
                template,
                BuildConnectionString(options.Value),
                loggerFactory.CreateLogger<LearnedPmsAdapter>(),
                db.GetOrCreateHmacSalt(template.SessionId),
                options.Value.SqlTrustServerCertificate,
                options.Value.SqlServerCertificateSha256),
            template => ValidateLiveSource(db, options.Value, template))
    {
    }

    internal ActivePmsAdapterRegistry(
        AgentStateDb db,
        AgentOptions options,
        ILogger<ActivePmsAdapterRegistry> logger,
        Func<LearnedPmsAdapterTemplate, ILocalPmsAdapter> adapterFactory,
        Func<LearnedPmsAdapterTemplate, string?> liveSourceValidator)
    {
        _db = db;
        _options = options;
        _logger = logger;
        _adapterFactory = adapterFactory;
        _liveSourceValidator = liveSourceValidator;
    }

    public AdapterActivationResult ActivateApproved(string sessionId)
    {
        lock (_gate)
        {
            if (_disposed)
                return new(AdapterActivationOutcome.Failed, "registry_disposed");
        }

        var validation = ValidateApproval(sessionId);
        if (!validation.IsValid)
        {
            _logger.LogWarning("core.learning.adapter_activation_rejected");
            return new(AdapterActivationOutcome.Rejected, validation.Reason);
        }

        var binding = validation.Binding!;
        var template = validation.Template!;

        lock (_gate)
        {
            if (_active is { } current && current.Binding == binding && !current.Retiring)
                return new(AdapterActivationOutcome.AlreadyActive, "exact_binding_already_active", binding);
        }

        ILocalPmsAdapter adapter;
        try
        {
            adapter = _adapterFactory(template);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "core.learning.adapter_construction_failed exception_type={ExceptionType}",
                ex.GetType().Name);
            return new(AdapterActivationOutcome.Failed, "adapter_construction_failed");
        }

        try
        {
            var liveHealth = adapter.CheckHealthAsync(CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            if (!liveHealth.IsHealthy)
            {
                DisposeAdapter(adapter);
                return new(AdapterActivationOutcome.Rejected, "live_schema_contract_invalid");
            }
        }
        catch (Exception ex)
        {
            DisposeAdapter(adapter);
            _logger.LogWarning(
                "core.learning.adapter_contract_failed exception_type={ExceptionType}",
                ex.GetType().Name);
            return new(AdapterActivationOutcome.Failed, "live_schema_contract_unavailable");
        }

        try
        {
            var approval = _db.GetLearningApproval(sessionId)!;
            if (approval.Phase == "approved")
                _db.UpdateLearningPhase(sessionId, "active");

            var activatedAt = DateTimeOffset.UtcNow.ToString("o");
            _db.UpsertLearnedAdapterActivation(
                binding.PharmacyId,
                binding.SessionId,
                binding.TemplateDigest,
                binding.ModelDigest,
                binding.ApprovedBy,
                binding.ApprovedAt.ToString("o"),
                activatedAt);
        }
        catch (Exception ex)
        {
            DisposeAdapter(adapter);
            _logger.LogWarning(
                "core.learning.adapter_persistence_failed exception_type={ExceptionType}",
                ex.GetType().Name);
            return new(AdapterActivationOutcome.Failed, "activation_persistence_failed");
        }

        try
        {
            SwapActive(new RegistryEntry(adapter, binding));
        }
        catch (Exception ex)
        {
            DisposeAdapter(adapter);
            _logger.LogWarning(
                "core.learning.adapter_swap_failed exception_type={ExceptionType}",
                ex.GetType().Name);
            return new(AdapterActivationOutcome.Failed, "registry_swap_failed");
        }

        try
        {
            _db.AppendLearningAudit(
                sessionId,
                "worker",
                "learned_adapter_activated",
                $"template:{DigestPrefix(binding.TemplateDigest)},model:{DigestPrefix(binding.ModelDigest)}",
                phiScrubbed: false);
        }
        catch (Exception ex)
        {
            // Activation receipt already committed. Keep the adapter active and
            // surface only the PHI-free exception type.
            _logger.LogWarning(
                "core.learning.adapter_audit_failed exception_type={ExceptionType}",
                ex.GetType().Name);
        }

        _logger.LogInformation("core.learning.adapter_active");
        return new(AdapterActivationOutcome.Activated, "approved_exact_binding", binding);
    }

    public ActivePmsAdapterLease? TryAcquire(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_disposed || _active is not { Retiring: false } entry) return null;
            if (entry.Health.RetryAfter is { } retryAfter && now < retryAfter) return null;
            entry.LeaseCount++;
            return new ActivePmsAdapterLease(entry.Adapter, entry.Binding, () => Release(entry));
        }
    }

    public void ReportHealthy(ActivePmsAdapterBinding binding, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_active is not { } entry || entry.Binding != binding || entry.Retiring) return;
            entry.Health = new RegistryHealth(0, null, now);
        }
    }

    public void ReportUnhealthy(
        ActivePmsAdapterBinding binding,
        DateTimeOffset now,
        string errorCategory)
    {
        lock (_gate)
        {
            if (_active is not { } entry || entry.Binding != binding || entry.Retiring) return;
            var failures = entry.Health.ConsecutiveFailures + 1;
            var seconds = Math.Min(30 * Math.Pow(2, Math.Min(failures - 1, 4)), MaxHealthBackoff.TotalSeconds);
            entry.Health = entry.Health with
            {
                ConsecutiveFailures = failures,
                RetryAfter = now + TimeSpan.FromSeconds(seconds),
            };
            _logger.LogWarning(
                "core.learning.adapter_unavailable failed={Failed}",
                failures);
        }
    }

    public ActivePmsAdapterStatus Snapshot(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_active is not { Retiring: false } entry)
                return new(false, null, null, 0, null, null);
            return new(
                HasActiveAdapter: true,
                SessionId: entry.Binding.SessionId,
                TemplateDigestPrefix: DigestPrefix(entry.Binding.TemplateDigest),
                ConsecutiveHealthFailures: entry.Health.ConsecutiveFailures,
                RetryAfter: entry.Health.RetryAfter,
                LastHealthyAt: entry.Health.LastHealthyAt);
        }
    }

    public ActivePmsAdapterBinding? CurrentBinding()
    {
        lock (_gate)
        {
            return _disposed || _active is not { Retiring: false } entry
                ? null
                : entry.Binding;
        }
    }

    public void Dispose()
    {
        ILocalPmsAdapter? adapterToDispose;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            adapterToDispose = RetireEntryLocked(_active);
            _active = null;
        }
        if (adapterToDispose is not null) DisposeAdapter(adapterToDispose);
    }

    private ApprovalValidation ValidateApproval(string sessionId)
    {
        var approval = _db.GetLearningApproval(sessionId);
        if (approval is null) return ApprovalValidation.Invalid("session_not_found");
        if (approval.Phase is not ("approved" or "active"))
            return ApprovalValidation.Invalid("human_approval_required");
        if (string.IsNullOrWhiteSpace(_options.PharmacyId) ||
            !string.Equals(approval.PharmacyId, _options.PharmacyId, StringComparison.Ordinal))
            return ApprovalValidation.Invalid("pharmacy_binding_mismatch");
        if (!IsHumanApproval(approval.ApprovedBy) ||
            !DateTimeOffset.TryParse(approval.ApprovedAt, out var approvedAt))
            return ApprovalValidation.Invalid("local_human_approval_missing");
        if (!IsSha256(approval.ModelDigest))
            return ApprovalValidation.Invalid("approved_model_digest_missing");

        var frozenPom = _db.GetPomSnapshot(sessionId);
        if (string.IsNullOrWhiteSpace(frozenPom))
            return ApprovalValidation.Invalid("frozen_pom_missing");
        var recomputedModelDigest = PomExporter.ComputeDigest(approval.PharmacyId, sessionId, frozenPom);
        if (!DigestEquals(approval.ModelDigest!, recomputedModelDigest))
            return ApprovalValidation.Invalid("model_digest_mismatch");

        var frozenBinding = ReadFrozenBinding(frozenPom);
        if (frozenBinding is null ||
            !string.Equals(frozenBinding.Value.SessionId, sessionId, StringComparison.Ordinal) ||
            !string.Equals(frozenBinding.Value.PharmacyId, approval.PharmacyId, StringComparison.Ordinal) ||
            !IsSha256(frozenBinding.Value.TemplateDigest) ||
            !IsSha256(frozenBinding.Value.SourceIdentityDigest) ||
            !IsSha256(frozenBinding.Value.SchemaContractDigest))
            return ApprovalValidation.Invalid("frozen_template_binding_missing");

        var template = new AdapterGenerator(_db).Describe(sessionId);
        if (template is null)
            return ApprovalValidation.Invalid("adapter_template_unavailable");
        if (!DigestEquals(template.TemplateDigest, frozenBinding.Value.TemplateDigest))
            return ApprovalValidation.Invalid("adapter_template_digest_mismatch");
        if (!DigestEquals(template.SourceIdentityDigest, frozenBinding.Value.SourceIdentityDigest) ||
            !DigestEquals(template.SchemaContractDigest, frozenBinding.Value.SchemaContractDigest))
            return ApprovalValidation.Invalid("adapter_source_contract_mismatch");
        var liveSourceFailure = _liveSourceValidator(template);
        if (liveSourceFailure is not null)
            return ApprovalValidation.Invalid(liveSourceFailure);

        var binding = new ActivePmsAdapterBinding(
            approval.PharmacyId,
            sessionId,
            template.TemplateDigest,
            approval.ModelDigest!,
            approval.ApprovedBy!,
            approvedAt);
        return new(true, "approved", binding, template);
    }

    private static (string SessionId, string PharmacyId, string TemplateDigest,
        string SourceIdentityDigest, string SchemaContractDigest)? ReadFrozenBinding(string pomJson)
    {
        try
        {
            using var document = JsonDocument.Parse(pomJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("sessionId", out var sessionElement) ||
                !root.TryGetProperty("pharmacyId", out var pharmacyElement) ||
                !root.TryGetProperty("learnedAdapterTemplate", out var templateElement) ||
                templateElement.ValueKind != JsonValueKind.Object ||
                !templateElement.TryGetProperty("templateDigest", out var digestElement) ||
                !templateElement.TryGetProperty("sourceIdentityDigest", out var sourceElement) ||
                !templateElement.TryGetProperty("schemaContractDigest", out var schemaElement))
                return null;
            var sessionId = sessionElement.GetString();
            var pharmacyId = pharmacyElement.GetString();
            var templateDigest = digestElement.GetString();
            var sourceIdentityDigest = sourceElement.GetString();
            var schemaContractDigest = schemaElement.GetString();
            return string.IsNullOrWhiteSpace(sessionId) ||
                   string.IsNullOrWhiteSpace(pharmacyId) ||
                   string.IsNullOrWhiteSpace(templateDigest) ||
                   string.IsNullOrWhiteSpace(sourceIdentityDigest) ||
                   string.IsNullOrWhiteSpace(schemaContractDigest)
                ? null
                : (sessionId, pharmacyId, templateDigest, sourceIdentityDigest, schemaContractDigest);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void SwapActive(RegistryEntry replacement)
    {
        ILocalPmsAdapter? adapterToDispose;
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ActivePmsAdapterRegistry));
            adapterToDispose = RetireEntryLocked(_active);
            _active = replacement;
        }
        if (adapterToDispose is not null) DisposeAdapter(adapterToDispose);
    }

    private void Release(RegistryEntry entry)
    {
        ILocalPmsAdapter? adapterToDispose = null;
        lock (_gate)
        {
            entry.LeaseCount = Math.Max(0, entry.LeaseCount - 1);
            if (entry.Retiring && entry.LeaseCount == 0 && !entry.Disposed)
            {
                entry.Disposed = true;
                adapterToDispose = entry.Adapter;
            }
        }
        if (adapterToDispose is not null) DisposeAdapter(adapterToDispose);
    }

    private static ILocalPmsAdapter? RetireEntryLocked(RegistryEntry? entry)
    {
        if (entry is null) return null;
        entry.Retiring = true;
        if (entry.LeaseCount != 0 || entry.Disposed) return null;
        entry.Disposed = true;
        return entry.Adapter;
    }

    private static void DisposeAdapter(ILocalPmsAdapter adapter)
    {
        if (adapter is IDisposable disposable) disposable.Dispose();
    }

    private static bool IsHumanApproval(string? approvedBy) =>
        !string.IsNullOrWhiteSpace(approvedBy) &&
        !string.Equals(approvedBy, "unknown", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(approvedBy, "system", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(approvedBy, "agent", StringComparison.OrdinalIgnoreCase);

    private static bool IsSha256(string? digest) =>
        digest is { Length: 64 } && digest.All(Uri.IsHexDigit);

    private static bool DigestEquals(string left, string right)
    {
        if (!IsSha256(left) || !IsSha256(right)) return false;
        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(left),
            Convert.FromHexString(right));
    }

    private static string DigestPrefix(string digest) =>
        digest.Length >= 12 ? digest[..12] : "invalid";

    private static string SanitizeCategory(string category)
    {
        var safe = new string(category
            .Where(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_')
            .Take(40)
            .ToArray());
        return string.IsNullOrEmpty(safe) ? "unknown" : safe;
    }

    private static string? ValidateLiveSource(
        AgentStateDb db,
        AgentOptions options,
        LearnedPmsAdapterTemplate template)
    {
        try
        {
            using var connection = new SqlConnection(BuildConnectionString(options));
            connection.Open();
            var salt = db.GetOrCreateHmacSalt(template.SessionId);
            var observed = SqlSourceIdentityVerifier.ComputeAsync(
                    connection,
                    salt,
                    options.SqlTrustServerCertificate,
                    options.SqlServerCertificateSha256,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            if (!string.Equals(observed.DatabaseName, template.DatabaseName, StringComparison.OrdinalIgnoreCase) ||
                !SqlSourceIdentityVerifier.FixedDigestEquals(
                    observed.Digest,
                    template.SourceIdentityDigest))
                return "live_source_identity_mismatch";
            return null;
        }
        catch
        {
            return "live_source_identity_unavailable";
        }
    }

    internal static string BuildConnectionString(AgentOptions options)
    {
        var csb = new SqlConnectionStringBuilder();
        if (!string.IsNullOrEmpty(options.SqlServer)) csb.DataSource = options.SqlServer;
        if (!string.IsNullOrEmpty(options.SqlDatabase)) csb.InitialCatalog = options.SqlDatabase;
        csb.ApplicationName = "SuavoLearnedRead";
        csb.MaxPoolSize = 1;
        csb.ConnectTimeout = 10;
        // Transparent driver reconnects can move an already-approved learned query to a
        // different SQL source without giving us a chance to re-verify its identity.
        // Force every recovery through LearnedPmsAdapter.EnsureConnectionOpenAsync.
        csb.ConnectRetryCount = 0;
        SqlConnectionSecurity.Apply(csb, options);
        if (!string.IsNullOrEmpty(options.SqlUser))
        {
            csb.UserID = options.SqlUser;
            csb.Password = options.SqlPassword;
        }
        else
        {
            csb.IntegratedSecurity = true;
        }
        return csb.ConnectionString;
    }

    private sealed class RegistryEntry
    {
        internal RegistryEntry(ILocalPmsAdapter adapter, ActivePmsAdapterBinding binding)
        {
            Adapter = adapter;
            Binding = binding;
        }

        internal ILocalPmsAdapter Adapter { get; }
        internal ActivePmsAdapterBinding Binding { get; }
        internal int LeaseCount { get; set; }
        internal bool Retiring { get; set; }
        internal bool Disposed { get; set; }
        internal RegistryHealth Health { get; set; } = new(0, null, null);
    }

    private sealed record RegistryHealth(
        int ConsecutiveFailures,
        DateTimeOffset? RetryAfter,
        DateTimeOffset? LastHealthyAt);

    private sealed record ApprovalValidation(
        bool IsValid,
        string Reason,
        ActivePmsAdapterBinding? Binding,
        LearnedPmsAdapterTemplate? Template)
    {
        internal static ApprovalValidation Invalid(string reason) => new(false, reason, null, null);
    }
}
