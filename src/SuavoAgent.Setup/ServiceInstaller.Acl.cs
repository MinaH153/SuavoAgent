using System.Security.AccessControl;
using SuavoAgent.Contracts.Security;
using SuavoAgent.Setup.Security;

namespace SuavoAgent.Setup;

/// <summary>
/// Owns purpose-specific Windows ACLs for binaries, runtime state, native
/// maintenance artifacts, and the de-privileged interactive Helper.
/// </summary>
internal static partial class ServiceInstaller
{
    internal enum ProtectedDirectoryKind
    {
        Install,
        Data,
        Maintenance,
    }

    internal static HandleBoundAclPolicy BuildProtectedAclPolicy(
        ProtectedDirectoryKind kind,
        bool directory,
        bool inherit,
        params HandleBoundAclAce[] additionalAces)
    {
        var inheritance = directory && inherit
            ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit
            : InheritanceFlags.None;
        var aces = new List<HandleBoundAclAce>
        {
            new(HandleBoundAcl.SystemSid, FileSystemRights.FullControl, inheritance),
            new(HandleBoundAcl.AdministratorsSid, FileSystemRights.FullControl, inheritance),
        };
        var coreRights = kind switch
        {
            ProtectedDirectoryKind.Install => FileSystemRights.ReadAndExecute,
            ProtectedDirectoryKind.Data => FileSystemRights.Modify,
            ProtectedDirectoryKind.Maintenance => (FileSystemRights?)null,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        if (coreRights is not null)
            aces.Add(new(CoreServiceIdentity.ServiceSid, coreRights.Value, inheritance));
        aces.AddRange(additionalAces);
        return new(HandleBoundAcl.SystemSid, aces);
    }

    /// <summary>
    /// Install binaries are immutable to the Core service. Core needs read/execute
    /// for its executable and appsettings, never write. Granting Modify here
    /// lets a compromised Core replace a later LocalSystem Broker/Watchdog and
    /// is a direct privilege-escalation path.
    /// </summary>
    public static void LockdownInstallDirectoryAcl(string path) =>
        LockdownDirectoryAcl(path, ProtectedDirectoryKind.Install);

    /// <summary>Runtime state is writable only by the exact Core service SID.</summary>
    public static void LockdownDataDirectoryAcl(string path) =>
        LockdownDirectoryAcl(path, ProtectedDirectoryKind.Data);

    /// <summary>
    /// Transaction journals, rollback cohorts, and the detached runner are
    /// writable only by Administrators and SYSTEM.
    /// </summary>
    public static void LockdownMaintenanceDirectoryAcl(string path) =>
        LockdownDirectoryAcl(path, ProtectedDirectoryKind.Maintenance);

    /// <summary>
    /// Exact reviewed native-vision cohort policy. Core and the interactive
    /// Helper can execute/read model assets; neither can replace them. Setup and
    /// SYSTEM retain maintenance authority.
    /// </summary>
    internal static bool TryLockdownVisionCohortAcl(string cohortRoot)
    {
        try
        {
            var inheritance = InheritanceFlags.ContainerInherit |
                              InheritanceFlags.ObjectInherit;
            var directory = BuildProtectedAclPolicy(
                ProtectedDirectoryKind.Maintenance,
                directory: true,
                inherit: true,
                new HandleBoundAclAce(
                    CoreServiceIdentity.ServiceSid,
                    FileSystemRights.ReadAndExecute,
                    inheritance),
                new HandleBoundAclAce(
                    HandleBoundAcl.UsersSid,
                    FileSystemRights.ReadAndExecute,
                    inheritance));
            var file = BuildProtectedAclPolicy(
                ProtectedDirectoryKind.Maintenance,
                directory: false,
                inherit: false,
                new HandleBoundAclAce(
                    CoreServiceIdentity.ServiceSid,
                    FileSystemRights.ReadAndExecute),
                new HandleBoundAclAce(
                    HandleBoundAcl.UsersSid,
                    FileSystemRights.ReadAndExecute));
            new HandleBoundAcl().ApplyTree(
                cohortRoot,
                directory,
                file,
                HandleBoundAcl.WithoutInheritance(directory),
                maximumEntries: 4_096);
            return true;
        }
        catch (Exception exception) when (exception is
                   IOException or UnauthorizedAccessException or
                   SystemException or ArgumentException)
        {
            ConsoleUI.WriteFail(
                "Windows could not secure the reviewed vision cohort. " +
                "Support code: SETUP-VISION-COHORT-ACL");
            return false;
        }
    }

    /// <summary>
    /// Applies and verifies the purpose-specific protected-directory DACL
    /// through identity-pinned, no-follow handles. No path is reopened for the
    /// privileged security mutation.
    /// </summary>
    private static void LockdownDirectoryAcl(string path, ProtectedDirectoryKind kind)
    {
        try
        {
            new HandleBoundAcl().ApplyTree(
                path,
                BuildProtectedAclPolicy(kind, directory: true, inherit: true),
                BuildProtectedAclPolicy(kind, directory: false, inherit: false),
                BuildProtectedAclPolicy(kind, directory: true, inherit: false));
            ConsoleUI.WriteOk($"ACL locked down: {path}");
        }
        catch (Exception ex)
        {
            // HARD FAIL (QA W2-C1): never write secrets to an unprotected directory.
            ConsoleUI.WriteFail(
                "Windows could not secure the SuavoAgent directory. " +
                "No credentials were written. Support code: SETUP-ACL-LOCKDOWN");
            throw new InvalidOperationException(
                "Directory ACL lockdown failed; refusing to write credentials to an unprotected directory.", ex);
        }
    }

    // The de-privileged Helper runs as the interactive user (CreateProcessAsUser). Its token is
    // ALWAYS a member of BUILTIN\Users (S-1-5-32-545) regardless of UAC token-filtering or logon
    // type — unlike the INTERACTIVE group (S-1-5-4), which a filtered/edge-case token can lack.
    // The native installer grants Users:RX for exactly this reason. SID form is
    // locale-independent, so localized Windows account names are never resolved.

    /// <summary>
    /// Install-dir read carve-out, applied AFTER LockdownDirectoryAcl pins the install dir to
    /// Admins/SYSTEM/the exact Core service SID. SuavoAgent.Helper.exe is a COMPRESSED single-file
    /// self-extracting apphost (publish: --self-contained PublishSingleFile + EnableCompression);
    /// at startup it RE-OPENS and self-extracts its OWN exe as the running (de-privileged) user.
    /// Without read access that open is denied → the Helper dies BEFORE its first log line and the
    /// Broker churns a fresh PID ~every 5s (helper_attached=false forever; root cause 2026-06-10,
    /// confirmed on box before the native GUI/console paths received this grant).
    /// Scoped to traverse-on-dir + RX-on-Helper.exe so appsettings.json (ApiKey + SQL creds) and
    /// the other service binaries stay UNREADABLE by local users.
    /// </summary>
    public static void GrantInteractiveHelperExeAccess(string installDir)
    {
        if (!TryGrantInteractiveHelperExeAccess(installDir))
            throw new InvalidOperationException(
                "Helper apphost ACL carve-out failed; refusing an unusable installation.");
    }

    internal static bool TryGrantInteractiveHelperExeAccess(string installDir)
    {
        try
        {
            // Revalidate and protect the complete cohort before adding either
            // carve-out. This rejects a preplanted root/child reparse point.
            LockdownInstallDirectoryAcl(installDir);
            var helperExe = Path.Combine(
                installDir,
                SuavoAgent.Diagnostics.HelperExeAclGrant.HelperExeName);
            new HandleBoundAcl().ApplyBatch(
                SuavoAgent.Diagnostics.HelperExeAclGrant.BuildMutations(
                    installDir,
                    includeHelper: File.Exists(helperExe)));
            ConsoleUI.WriteOk("Helper apphost readable by the interactive user (single-file self-extract); appsettings stays protected");
            return true;
        }
        catch (Exception)
        {
            ConsoleUI.WriteWarn(
                "Windows could not grant the signed Helper its minimum apphost read access. " +
                "The Helper will remain unavailable. Support code: SETUP-HELPER-APPHOST-ACL");
            return false;
        }
    }

    // The Helper's DATA-dir least-privilege carve-out, applied AFTER LockdownDirectoryAcl.
    // Deliberately NOT an inherited read on the data-dir root: state.db is plaintext
    // SQLite (PHI on a PMS box) and state.key is machine-scope DPAPI (any local
    // reader could decrypt), and SQLite recreates -wal/-shm constantly so even a
    // strip-after-grant would reopen a read window on every checkpoint. So:
    //   root            -> Users (RX), THIS DIR ONLY (traverse + list, no file reads)
    //   logs\helper\, diagnostics\helper\ -> Users (OI)(CI)M — the ONLY
    //     user-writable log/journal dirs. The logs\ and diagnostics\ roots stay
    //     service-only (traverse) so a local user can never plant junctions or
    //     links where SYSTEM (Broker/Watchdog) appends — log-dir EoP class
    //     (Codex review 2026-06-10 Q2).
    //   honeytokens\    -> Users (OI)(CI)M (decoy bait — user-touchable by design)
    //   actuation.json / pioneerrx.json / honeytoken-attribution.json
    //     -> Users (R) per-file when present (flows that create or atomically
    //     rewrite these later must re-grant — replace drops per-file ACEs).
    public static void GrantInteractiveHelperAccess(string dataDir)
    {
        if (!TryGrantInteractiveHelperAccess(dataDir))
            throw new InvalidOperationException(
                "Helper data ACL carve-out failed; refusing an unusable installation.");
    }

    internal static bool TryGrantInteractiveHelperAccess(string dataDir)
    {
        try
        {
            // First reject every pre-existing redirect. Then create the known
            // subdirectories under the protected root and re-run the no-follow
            // tree boundary before opening all carve-out handles as one batch.
            LockdownDataDirectoryAcl(dataDir);
            var specs = BuildInteractiveAclSpecs(dataDir);
            foreach (var spec in specs.Where(spec => spec.EnsureDirectory))
                Directory.CreateDirectory(spec.Target);
            LockdownDataDirectoryAcl(dataDir);

            foreach (var spec in specs.Where(spec => spec.ApplyRecursively))
            {
                new HandleBoundAcl().ApplyTree(
                    spec.Target,
                    BuildProtectedAclPolicy(
                        ProtectedDirectoryKind.Data,
                        directory: true,
                        inherit: true,
                        new HandleBoundAclAce(
                            HandleBoundAcl.UsersSid,
                            spec.UsersRights,
                            InheritanceFlags.ContainerInherit |
                            InheritanceFlags.ObjectInherit)),
                    BuildProtectedAclPolicy(
                        ProtectedDirectoryKind.Data,
                        directory: false,
                        inherit: false,
                        new HandleBoundAclAce(
                            HandleBoundAcl.UsersSid,
                            spec.UsersRights)),
                    BuildProtectedAclPolicy(
                        ProtectedDirectoryKind.Data,
                        directory: true,
                        inherit: false));
            }
            var mutations = specs
                .Where(spec => !spec.ApplyRecursively)
                .Where(spec => spec.IsDirectory || File.Exists(spec.Target))
                .Select(spec => new HandleBoundAclMutation(
                    spec.Target,
                    spec.IsDirectory,
                    BuildProtectedAclPolicy(
                        ProtectedDirectoryKind.Data,
                        spec.IsDirectory,
                        inherit: spec.IsDirectory,
                        new HandleBoundAclAce(
                            HandleBoundAcl.UsersSid,
                            spec.UsersRights,
                            spec.UsersInheritance))))
                .ToArray();
            new HandleBoundAcl().ApplyBatch(mutations);
            ConsoleUI.WriteOk("Helper (interactive user) data-dir carve-out applied: traverse + logs/diagnostics write + config reads");
            return true;
        }
        catch (Exception)
        {
            ConsoleUI.WriteWarn(
                "Windows could not grant the Helper its minimum data access. " +
                "The Helper will remain unavailable. Support code: SETUP-HELPER-DATA-ACL");
            return false;
        }
    }

    /// <summary>
    /// Strict ACL repair used by the native maintenance host. Unlike the normal
    /// install-time warning wrappers, every carve-out must succeed or repair exits
    /// nonzero. Callers validate the immutable binary cohort before entering here.
    /// </summary>
    internal static bool ReassertMaintenanceAcls(string installDir, string dataDir)
    {
        try
        {
            Directory.CreateDirectory(installDir);
            Directory.CreateDirectory(dataDir);
            LockdownInstallDirectoryAcl(installDir);
            if (!TryGrantInteractiveHelperExeAccess(installDir)) return false;
            LockdownDataDirectoryAcl(dataDir);
            if (!VisionRegistryProvisioner.ProvisionAndRetireLegacy(dataDir)) return false;
            if (!TryGrantInteractiveHelperAccess(dataDir)) return false;
            if (!ReleaseOcrCohortProvisioner.ReassertInstalledCohortAcls(
                    dataDir,
                    TryLockdownVisionCohortAcl))
                return false;
            return true;
        }
        catch (Exception)
        {
            ConsoleUI.WriteFail(
                "Native repair could not reassert the protected directory permissions. " +
                "Support code: SETUP-MAINTENANCE-ACL");
            return false;
        }
    }

    internal sealed record InteractiveAclSpec(
        string Target,
        bool IsDirectory,
        bool EnsureDirectory,
        FileSystemRights UsersRights,
        InheritanceFlags UsersInheritance = InheritanceFlags.None,
        bool ApplyRecursively = false);

    /// <summary>Pure least-privilege carve-out specification, unit-testable
    /// without invoking Windows security APIs.</summary>
    internal static IReadOnlyList<InteractiveAclSpec> BuildInteractiveAclSpecs(string dataDir) =>
    [
        new(dataDir, true, true, FileSystemRights.ReadAndExecute),
        new(Path.Combine(dataDir, "logs"), true, true, FileSystemRights.ReadAndExecute),
        new(
            Path.Combine(dataDir, "logs", "helper"),
            true,
            true,
            FileSystemRights.Modify,
            InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit,
            ApplyRecursively: true),
        new(Path.Combine(dataDir, "diagnostics"), true, true, FileSystemRights.ReadAndExecute),
        new(
            Path.Combine(dataDir, "diagnostics", "helper"),
            true,
            true,
            FileSystemRights.Modify,
            InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit,
            ApplyRecursively: true),
        // Honeytokens are decoy bait the Helper plants and watches — user-touchable
        // is their entire purpose; no SYSTEM process writes files here.
        new(
            Path.Combine(dataDir, "honeytokens"),
            true,
            true,
            FileSystemRights.Modify,
            InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit,
            ApplyRecursively: true),
        // Signed observation leases and their PHI-free local binding are
        // readable by Helper/browser-host but writable only by services/admins.
        new(
            Path.Combine(
                dataDir,
                SuavoAgent.Contracts.Security.ObservationActivationAuthority.StateDirectoryName),
            true,
            true,
            FileSystemRights.ReadAndExecute,
            InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit,
            ApplyRecursively: true),
        new(Path.Combine(dataDir, "actuation.json"), false, false, FileSystemRights.Read),
        new(Path.Combine(dataDir, "pioneerrx.json"), false, false, FileSystemRights.Read),
        new(
            Path.Combine(dataDir, "honeytoken-attribution.json"),
            false,
            false,
            FileSystemRights.Read),
    ];
}
