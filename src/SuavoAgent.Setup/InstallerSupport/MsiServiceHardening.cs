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
    void Save(IReadOnlyDictionary<string, InstallerServiceConfiguration> snapshots);
    IReadOnlyDictionary<string, InstallerServiceConfiguration>? Load();
    void Delete();
}

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

    internal MsiServiceHardeningTransaction(
        IInstallerServiceConfigurationSession session,
        IInstallerServiceHardeningJournal journal)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
    }

    internal MsiServiceHardeningExitCode Execute()
    {
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

        try
        {
            // Never overwrite or silently commit a prior interrupted
            // transaction, including the already-target idempotent path.
            if (_journal.Load() is not null)
                return MsiServiceHardeningExitCode.JournalFailed;
        }
        catch (Exception)
        {
            return MsiServiceHardeningExitCode.JournalFailed;
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
            _journal.Save(snapshots);
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
                _journal.Delete();
                return MsiServiceHardeningExitCode.ApplyFailedRolledBack;
            }
            catch (Exception)
            {
                // Leave the durable snapshot for the MSI rollback action.
                return MsiServiceHardeningExitCode.RollbackFailed;
            }
        }
    }

    internal MsiServiceHardeningExitCode ExecutePersistedRollback()
    {
        IReadOnlyDictionary<string, InstallerServiceConfiguration>? snapshots;
        try
        {
            snapshots = _journal.Load();
        }
        catch (Exception)
        {
            return MsiServiceHardeningExitCode.RollbackFailed;
        }

        // The rollback action is queued before the forward action. A missing
        // journal therefore means the forward action never mutated the SCM.
        if (snapshots is null)
            return MsiServiceHardeningExitCode.Success;

        if (!RollBack(ServiceNames, snapshots))
            return MsiServiceHardeningExitCode.RollbackFailed;

        try
        {
            _journal.Delete();
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
    internal const string ApplySwitch = "--msi-apply-service-hardening";
    internal const string RollbackSwitch = "--msi-rollback-service-hardening";
    internal const string CommitSwitch = "--msi-commit-service-hardening";

    internal static readonly IReadOnlyList<string> Switches =
    [
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
            static () => FileInstallerServiceHardeningJournal.CreateForInstalledHost());

    internal static int Run(
        IReadOnlyList<string>? arguments,
        bool isWindows,
        Func<IInstallerServiceConfigurationSession> createSession,
        Func<IInstallerServiceHardeningJournal> createJournal)
    {
        if (arguments is null ||
            arguments.Count != 1 ||
            !IsKnownSwitch(arguments[0]))
        {
            return (int)MsiServiceHardeningExitCode.InvalidArguments;
        }

        if (!isWindows)
            return (int)MsiServiceHardeningExitCode.UnsupportedHost;

        ArgumentNullException.ThrowIfNull(createSession);
        ArgumentNullException.ThrowIfNull(createJournal);
        var requestedSwitch = arguments[0];
        try
        {
            var journal = createJournal();
            if (string.Equals(requestedSwitch, CommitSwitch, StringComparison.OrdinalIgnoreCase))
            {
                // Commit cleanup does not need SCM authority and therefore does
                // not open service handles unnecessarily.
                return (int)Commit(journal);
            }

            using var session = createSession();
            var transaction = new MsiServiceHardeningTransaction(session, journal);
            return string.Equals(requestedSwitch, ApplySwitch, StringComparison.OrdinalIgnoreCase)
                ? (int)transaction.Execute()
                : (int)transaction.ExecutePersistedRollback();
        }
        catch (Exception)
        {
            // The MSI log receives only this bounded exit code. Native error
            // text, environment values, credentials, and PHI are never emitted.
            return string.Equals(requestedSwitch, RollbackSwitch, StringComparison.OrdinalIgnoreCase)
                ? (int)MsiServiceHardeningExitCode.RollbackFailed
                : string.Equals(requestedSwitch, CommitSwitch, StringComparison.OrdinalIgnoreCase)
                    ? (int)MsiServiceHardeningExitCode.CommitFailed
                    : (int)MsiServiceHardeningExitCode.SnapshotFailed;
        }
    }

    private static bool IsKnownSwitch(string argument) =>
        Switches.Any(candidate => string.Equals(
            candidate,
            argument,
            StringComparison.OrdinalIgnoreCase));

    private static MsiServiceHardeningExitCode Commit(
        IInstallerServiceHardeningJournal journal)
    {
        // Commit has no service session, but reusing the transaction's cleanup
        // behavior would otherwise require opening SCM with elevated rights.
        try
        {
            journal.Delete();
            return MsiServiceHardeningExitCode.Success;
        }
        catch (Exception)
        {
            return MsiServiceHardeningExitCode.CommitFailed;
        }
    }
}
