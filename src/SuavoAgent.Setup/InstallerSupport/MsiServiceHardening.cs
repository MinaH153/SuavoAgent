using SuavoAgent.Setup.Maintenance;

namespace SuavoAgent.Setup.InstallerSupport;

internal readonly record struct InstallerServiceConfiguration(
    bool DelayedAutoStart,
    uint ServiceSidType);

internal interface IInstallerServiceConfigurationSession : IDisposable
{
    InstallerServiceConfiguration Read(string serviceName);
    void Write(string serviceName, InstallerServiceConfiguration configuration);
}

internal interface IInstallerServiceHardeningJournal
{
    void SavePending(
        string invocationId,
        IReadOnlyDictionary<string, InstallerServiceConfiguration> snapshots);
    InstallerServiceHardeningJournalState? Load();
    void MarkCommitted(string invocationId);
    void Delete(string invocationId, InstallerTransactionJournalPhase phase);
}

internal enum InstallerTransactionJournalPhase
{
    Pending,
    Committed,
}

internal sealed record InstallerServiceHardeningJournalState(
    string InvocationId,
    InstallerTransactionJournalPhase Phase,
    IReadOnlyDictionary<string, InstallerServiceConfiguration> Snapshots);

/// <summary>
/// Applies the two service settings that Windows Installer's service-config
/// tables cannot safely own. The transaction snapshots every service before the
/// first mutation and restores every touched service in reverse order if an
/// apply or verification step fails.
/// </summary>
internal sealed class MsiServiceHardeningTransaction
{
    internal const uint ServiceSidTypeUnrestricted = 1;

    internal static readonly IReadOnlyList<string> ServiceNames =
    [
        "SuavoAgent.Core",
        "SuavoAgent.Broker",
        "SuavoAgent.Watchdog",
    ];

    internal static readonly InstallerServiceConfiguration Target = new(
        DelayedAutoStart: true,
        ServiceSidType: ServiceSidTypeUnrestricted);

    private readonly IInstallerServiceConfigurationSession _session;
    private readonly IInstallerServiceHardeningJournal _journal;
    private readonly IMsiInstallerTransactionActivation _activation;

    internal MsiServiceHardeningTransaction(
        IInstallerServiceConfigurationSession session,
        IInstallerServiceHardeningJournal journal,
        IMsiInstallerTransactionActivation activation)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _activation = activation ?? throw new ArgumentNullException(nameof(activation));
    }

    internal MsiServiceHardeningExitCode Execute(string invocationId)
    {
        if (!MsiInstallerInvocation.IsValidInvocationId(invocationId))
            return MsiServiceHardeningExitCode.JournalFailed;
        try { _activation.RequireCurrent(invocationId); }
        catch { return MsiServiceHardeningExitCode.JournalFailed; }

        try
        {
            var existing = _journal.Load();
            if (existing?.Phase == InstallerTransactionJournalPhase.Committed)
            {
                // A prior successful invocation may have crashed after sealing
                // but before its ignore-only deletion. It is cleanup-only.
                _journal.Delete(
                    existing.InvocationId,
                    InstallerTransactionJournalPhase.Committed);
            }
            else if (existing is not null)
            {
                // Never infer whether a prior pending invocation mutated SCM.
                return MsiServiceHardeningExitCode.JournalFailed;
            }
        }
        catch (Exception)
        {
            return MsiServiceHardeningExitCode.JournalFailed;
        }

        IReadOnlyDictionary<string, InstallerServiceConfiguration> snapshots;
        try
        {
            snapshots = ServiceNames.ToDictionary(
                static name => name,
                name => _session.Read(name),
                StringComparer.Ordinal);
        }
        catch (Exception)
        {
            return MsiServiceHardeningExitCode.SnapshotFailed;
        }

        var pending = ServiceNames
            .Where(serviceName => snapshots[serviceName] != Target)
            .ToArray();
        if (pending.Length == 0)
            return MsiServiceHardeningExitCode.Success;

        try
        {
            // The deferred process exits before MSI knows whether a later action
            // will fail. Persist the exact pre-change state before the first SCM
            // mutation so the paired rollback action can restore it.
            _journal.SavePending(invocationId, snapshots);
        }
        catch (Exception)
        {
            return MsiServiceHardeningExitCode.JournalFailed;
        }

        var touched = new List<string>(pending.Length);
        try
        {
            foreach (var serviceName in pending)
            {
                // Record the service before the native call. If the first
                // ChangeServiceConfig2 succeeds and the second fails, rollback
                // still restores both fields for this partially changed service.
                touched.Add(serviceName);
                _session.Write(serviceName, Target);
                if (_session.Read(serviceName) != Target)
                    throw new InvalidOperationException("Service hardening verification failed.");
            }

            return MsiServiceHardeningExitCode.Success;
        }
        catch (Exception)
        {
            if (!RollBack(touched, snapshots))
                return MsiServiceHardeningExitCode.RollbackFailed;
            try
            {
                _journal.Delete(
                    invocationId,
                    InstallerTransactionJournalPhase.Pending);
                return MsiServiceHardeningExitCode.ApplyFailedRolledBack;
            }
            catch (Exception)
            {
                // Leave the durable snapshot for the MSI rollback action.
                return MsiServiceHardeningExitCode.RollbackFailed;
            }
        }
    }

    internal MsiServiceHardeningExitCode ExecutePersistedRollback(
        string invocationId)
    {
        if (!MsiInstallerInvocation.IsValidInvocationId(invocationId))
            return MsiServiceHardeningExitCode.RollbackFailed;
        try { _activation.RequireCurrent(invocationId); }
        catch { return MsiServiceHardeningExitCode.RollbackFailed; }

        InstallerServiceHardeningJournalState? journal;
        try
        {
            journal = _journal.Load();
        }
        catch (Exception)
        {
            return MsiServiceHardeningExitCode.RollbackFailed;
        }

        // The rollback action is queued before the forward action. A missing
        // journal therefore means the forward action never mutated the SCM.
        if (journal is null)
            return MsiServiceHardeningExitCode.Success;

        if (journal.Phase != InstallerTransactionJournalPhase.Pending ||
            !string.Equals(
                journal.InvocationId,
                invocationId,
                StringComparison.Ordinal))
            return MsiServiceHardeningExitCode.RollbackFailed;

        if (!RollBack(ServiceNames, journal.Snapshots))
            return MsiServiceHardeningExitCode.RollbackFailed;

        try
        {
            _journal.Delete(
                invocationId,
                InstallerTransactionJournalPhase.Pending);
            return MsiServiceHardeningExitCode.Success;
        }
        catch (Exception)
        {
            return MsiServiceHardeningExitCode.RollbackFailed;
        }
    }

    private bool RollBack(
        IReadOnlyList<string> touched,
        IReadOnlyDictionary<string, InstallerServiceConfiguration> snapshots)
    {
        var succeeded = true;
        for (var index = touched.Count - 1; index >= 0; index--)
        {
            var serviceName = touched[index];
            try
            {
                var original = snapshots[serviceName];
                _session.Write(serviceName, original);
                if (_session.Read(serviceName) != original)
                    succeeded = false;
            }
            catch (Exception)
            {
                // Continue restoring earlier services even if one rollback
                // operation fails. The nonzero exit then fails the MSI closed.
                succeeded = false;
            }
        }

        return succeeded;
    }
}

internal enum MsiServiceHardeningExitCode
{
    Success = 0,
    InvalidArguments = 40,
    UnsupportedHost = 41,
    SnapshotFailed = 42,
    ApplyFailedRolledBack = 43,
    RollbackFailed = 44,
    JournalFailed = 45,
    CommitFailed = 46,
}

internal static class MsiServiceHardeningRunner
{
    internal const string ArmSwitch = "--msi-arm-installer-transaction";
    internal const string ApplySwitch = "--msi-apply-service-hardening";
    internal const string RollbackSwitch = "--msi-rollback-service-hardening";
    internal const string CommitSwitch = "--msi-commit-service-hardening";

    internal static readonly IReadOnlyList<string> Switches =
    [
        ArmSwitch,
        ApplySwitch,
        RollbackSwitch,
        CommitSwitch,
    ];

    internal static bool IsRequested(IReadOnlyList<string>? arguments) =>
        arguments?.Any(IsKnownSwitch) == true;

    internal static int Run(IReadOnlyList<string> arguments) =>
        Run(
            arguments,
            OperatingSystem.IsWindows(),
            static () => new Win32InstallerServiceConfigurationSession(),
            static installDirectory =>
                FileInstallerServiceHardeningJournal.CreateForInstallDirectory(
                    installDirectory),
            static installDirectory =>
                FileMsiInstallerTransactionActivation.CreateForInstallDirectory(
                    installDirectory),
            static () => Release1MsiInstallMarkerTransaction.AcquireProofLock(
                Release1MsiInstallMarkerStore.DefaultProofDirectory()),
            static () =>
                Release1MsiInstallMarkerTransaction
                    .RequireSettledForArmOrFinalization(
                        Release1MsiInstallMarkerStore.DefaultProofDirectory()));

    internal static int Run(
        IReadOnlyList<string>? arguments,
        bool isWindows,
        Func<IInstallerServiceConfigurationSession> createSession,
        Func<string, IInstallerServiceHardeningJournal> createJournal,
        Func<string, IMsiInstallerTransactionActivation> createActivation,
        Func<IDisposable> acquireTransactionGate,
        Action requireMarkerTransactionSettled)
    {
        if (arguments is null ||
            arguments.Count != 2 ||
            !IsKnownSwitch(arguments[0]) ||
            !MsiInstallerInvocation.TryParse(arguments[1], out var invocation))
        {
            return (int)MsiServiceHardeningExitCode.InvalidArguments;
        }

        if (!isWindows)
            return (int)MsiServiceHardeningExitCode.UnsupportedHost;

        ArgumentNullException.ThrowIfNull(createSession);
        ArgumentNullException.ThrowIfNull(createJournal);
        ArgumentNullException.ThrowIfNull(createActivation);
        ArgumentNullException.ThrowIfNull(acquireTransactionGate);
        ArgumentNullException.ThrowIfNull(requireMarkerTransactionSettled);
        var requestedSwitch = arguments[0];
        try
        {
            var activation = createActivation(invocation.InstallDirectory);
            var journal = createJournal(invocation.InstallDirectory);
            if (string.Equals(requestedSwitch, ArmSwitch, StringComparison.OrdinalIgnoreCase))
            {
                using var transactionGate = acquireTransactionGate();
                activation.RequireAbsent();
                SettleServiceJournalForArm(journal);
                requireMarkerTransactionSettled();
                activation.Arm(invocation.InvocationId);
                return (int)MsiServiceHardeningExitCode.Success;
            }

            if (string.Equals(requestedSwitch, CommitSwitch, StringComparison.OrdinalIgnoreCase))
            {
                // Commit cleanup does not need SCM authority and therefore does
                // not open service handles unnecessarily.
                var commitResult = CommitJournal(
                    journal,
                    activation,
                    invocation.InvocationId);
                if (commitResult != MsiServiceHardeningExitCode.Success)
                    return (int)commitResult;
                return (int)FinalizeTransaction(
                    activation,
                    invocation.InvocationId,
                    acquireTransactionGate,
                    requireMarkerTransactionSettled,
                    MsiServiceHardeningExitCode.CommitFailed);
            }

            using var session = createSession();
            var transaction = new MsiServiceHardeningTransaction(
                session,
                journal,
                activation);
            if (string.Equals(
                    requestedSwitch,
                    ApplySwitch,
                    StringComparison.OrdinalIgnoreCase))
                return (int)transaction.Execute(invocation.InvocationId);

            var rollbackResult = transaction.ExecutePersistedRollback(
                invocation.InvocationId);
            if (rollbackResult != MsiServiceHardeningExitCode.Success)
                return (int)rollbackResult;
            return (int)FinalizeTransaction(
                activation,
                invocation.InvocationId,
                acquireTransactionGate,
                requireMarkerTransactionSettled,
                MsiServiceHardeningExitCode.RollbackFailed);
        }
        catch (Exception)
        {
            // The MSI log receives only this bounded exit code. Native error
            // text, environment values, credentials, and PHI are never emitted.
            return string.Equals(requestedSwitch, RollbackSwitch, StringComparison.OrdinalIgnoreCase)
                ? (int)MsiServiceHardeningExitCode.RollbackFailed
                : string.Equals(requestedSwitch, CommitSwitch, StringComparison.OrdinalIgnoreCase)
                    ? (int)MsiServiceHardeningExitCode.CommitFailed
                    : string.Equals(requestedSwitch, ArmSwitch, StringComparison.OrdinalIgnoreCase)
                        ? (int)MsiServiceHardeningExitCode.JournalFailed
                        : (int)MsiServiceHardeningExitCode.SnapshotFailed;
        }
    }

    private static bool IsKnownSwitch(string argument) =>
        Switches.Any(candidate => string.Equals(
            candidate,
            argument,
            StringComparison.OrdinalIgnoreCase));

    private static void SettleServiceJournalForArm(
        IInstallerServiceHardeningJournal journal)
    {
        var state = journal.Load();
        if (state is null)
            return;
        if (state.Phase != InstallerTransactionJournalPhase.Committed)
            throw new InvalidDataException(
                "A pending service-hardening transaction blocks this invocation.");
        journal.Delete(
            state.InvocationId,
            InstallerTransactionJournalPhase.Committed);
    }

    private static MsiServiceHardeningExitCode CommitJournal(
        IInstallerServiceHardeningJournal journal,
        IMsiInstallerTransactionActivation activation,
        string invocationId)
    {
        // Commit has no service session, but reusing the transaction's cleanup
        // behavior would otherwise require opening SCM with elevated rights.
        try
        {
            activation.RequireCurrent(invocationId);
            var state = journal.Load();
            if (state is not null &&
                (!string.Equals(
                     state.InvocationId,
                     invocationId,
                     StringComparison.Ordinal) ||
                 state.Phase is not (
                     InstallerTransactionJournalPhase.Pending or
                     InstallerTransactionJournalPhase.Committed)))
                return MsiServiceHardeningExitCode.CommitFailed;

            var succeeded = true;
            if (state?.Phase == InstallerTransactionJournalPhase.Pending)
            {
                try { journal.MarkCommitted(invocationId); }
                catch { succeeded = false; }
            }
            if (succeeded && state is not null)
            {
                try
                {
                    journal.Delete(
                        invocationId,
                        InstallerTransactionJournalPhase.Committed);
                }
                catch { succeeded = false; }
            }

            return succeeded
                ? MsiServiceHardeningExitCode.Success
                : MsiServiceHardeningExitCode.CommitFailed;
        }
        catch (Exception)
        {
            return MsiServiceHardeningExitCode.CommitFailed;
        }
    }

    private static MsiServiceHardeningExitCode FinalizeTransaction(
        IMsiInstallerTransactionActivation activation,
        string invocationId,
        Func<IDisposable> acquireTransactionGate,
        Action requireMarkerTransactionSettled,
        MsiServiceHardeningExitCode failureCode)
    {
        try
        {
            using var transactionGate = acquireTransactionGate();
            activation.RequireCurrent(invocationId);
            requireMarkerTransactionSettled();
            activation.Disarm(invocationId);
            return MsiServiceHardeningExitCode.Success;
        }
        catch
        {
            // A token is intentionally stranded whenever either journal is
            // pending or cleanup fails. The next invocation must refuse it.
            return failureCode;
        }
    }
}
