using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using SuavoAgent.Contracts.Vision;

namespace SuavoAgent.Contracts.Security;

public enum VisionRegistryReadStatus
{
    Missing,
    Present,
    Invalid,
}

public sealed record VisionRegistryReadResult(
    VisionRegistryReadStatus Status,
    string Code,
    string? Value = null);

public sealed record VisionRegistryProvisionResult(
    bool StatePreserved,
    bool StateCleared,
    string Code,
    string? InvalidStateSha256 = null);

/// <summary>
/// Machine-wide authority for the single vision configuration value. Setup is
/// the only component allowed to create or repair the key. Core may query and
/// replace the value, while the interactive Helper and other local users are
/// read-only.
/// </summary>
public static class VisionRegistryAuthority
{
    public const string KeyPath = @"SOFTWARE\SuavoAgent\Vision";
    public const string ValueName = "State";
    public const int MaximumStateCharacters = 64 * 1024;

    private const string SystemSid = "S-1-5-18";
    private const string AdministratorsSid = "S-1-5-32-544";
    private const string BuiltinUsersSid = "S-1-5-32-545";

    [SupportedOSPlatform("windows")]
    public static RegistryRights CoreRights => RegistryRights.ReadKey | RegistryRights.SetValue;

    /// <summary>
    /// Creates or repairs the exact protected key. If the pre-existing ACL was
    /// not already exact, its state is discarded: an untrusted local writer may
    /// have forged it before repair, so preserving it would convert ACL repair
    /// into configuration activation.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static VisionRegistryProvisionResult ProvisionAndRepair(
        string dataDirectory,
        Func<VisionRegistryProvisionResult, bool>? persistBeforeClear = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "The vision registry authority exists only on Windows.");

        using var root = RegistryKey.OpenBaseKey(
            RegistryHive.LocalMachine,
            RegistryView.Registry64);
        var wasExact = false;
        string? priorStringState = null;
        var priorStateExisted = false;
        var priorStateWasString = false;
        try
        {
            using var existing = root.OpenSubKey(KeyPath, RegistryRights.ReadKey);
            wasExact = existing is not null && TryVerifyKeyAcl(existing, out _);
            if (existing is not null && existing.GetValueNames().Any(
                    name => string.Equals(name, ValueName, StringComparison.Ordinal)))
            {
                priorStateExisted = true;
                if (existing.GetValueKind(ValueName) == RegistryValueKind.String)
                {
                    priorStateWasString = true;
                    priorStringState = existing.GetValue(
                        ValueName,
                        null,
                        RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                }
            }
        }
        catch (Exception exception) when (exception is
                   UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // An unreadable or malformed prior key is untrusted. The elevated
            // repair path replaces its ACL and drops every prior value below.
        }
        using var key = root.CreateSubKey(
            KeyPath,
            RegistryKeyPermissionCheck.ReadWriteSubTree,
            RegistryOptions.None,
            CreateExactSecurity())
            ?? throw new InvalidOperationException(
                "The vision registry authority could not be created.");

        key.SetAccessControl(CreateExactSecurity());
        if (!TryVerifyKeyAcl(key, out var aclCode))
            throw new InvalidOperationException(
                $"The vision registry authority ACL is invalid ({aclCode}).");

        if (!priorStateExisted && key.GetValueNames().Any(
                name => string.Equals(name, ValueName, StringComparison.Ordinal)))
        {
            priorStateExisted = true;
            priorStateWasString = key.GetValueKind(ValueName) == RegistryValueKind.String;
            if (priorStateWasString)
            {
                priorStringState = key.GetValue(
                    ValueName,
                    null,
                    RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            }
        }

        var removedExtraneousEntry = key.GetSubKeyNames().Length > 0 ||
                                     key.GetValueNames().Any(name =>
                                         !string.Equals(name, ValueName, StringComparison.Ordinal));
        foreach (var subKeyName in key.GetSubKeyNames())
            key.DeleteSubKeyTree(subKeyName, throwOnMissingSubKey: false);

        foreach (var valueName in key.GetValueNames())
        {
            if (!string.Equals(valueName, ValueName, StringComparison.Ordinal))
            {
                key.DeleteValue(valueName, throwOnMissingValue: false);
            }
        }

        var disposition = EvaluateStateForRepair(
            wasExact,
            priorStateExisted,
            priorStateWasString,
            priorStringState,
            dataDirectory,
            removedExtraneousEntry);
        if (!disposition.StatePreserved)
        {
            // Persist the visible repair evidence before destroying an invalid
            // value. If persistence fails or the process dies first, the value
            // remains for the next repair run; a cleared state can therefore
            // never lose its only durable explanation in the crash window.
            if (!TryPersistRepairEvidence(disposition, persistBeforeClear))
            {
                throw new IOException(
                    "The vision registry repair receipt could not be persisted.");
            }
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }

        key.Flush();
        if (!TryVerifyKeyAcl(key, out aclCode))
            throw new InvalidOperationException(
                $"The vision registry authority ACL changed during repair ({aclCode}).");
        return disposition;
    }

    internal static bool TryPersistRepairEvidence(
        VisionRegistryProvisionResult disposition,
        Func<VisionRegistryProvisionResult, bool>? persistBeforeClear) =>
        !disposition.StateCleared ||
        persistBeforeClear is not null && persistBeforeClear(disposition);

    internal static VisionRegistryProvisionResult EvaluateStateForRepair(
        bool aclWasExact,
        bool stateExisted,
        bool stateWasString,
        string? state,
        string dataDirectory,
        bool removedExtraneousEntry = false)
    {
        if (!stateExisted)
        {
            return new(
                false,
                false,
                removedExtraneousEntry
                    ? "vision_registry_extraneous_entries_repaired"
                    : "vision_registry_ready_default_disabled");
        }
        if (!aclWasExact)
        {
            return new(
                false,
                true,
                "vision_registry_untrusted_acl_state_quarantined",
                Digest(state));
        }
        var parsed = stateWasString && state is { Length: > 0 and <= MaximumStateCharacters } &&
                     !state.Contains('\0', StringComparison.Ordinal)
            ? VisionConfigurationStateCodec.Parse(state, dataDirectory)
            : null;
        if (parsed is null || !parsed.IsValid)
        {
            return new(
                false,
                true,
                "vision_registry_invalid_state_quarantined",
                Digest(state));
        }
        return new(
            true,
            false,
            removedExtraneousEntry
                ? "vision_registry_extraneous_entries_repaired"
                : "vision_registry_valid_state_preserved");
    }

    private static string? Digest(string? value) => value is null
        ? null
        : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    public static VisionRegistryReadResult ReadState()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new(
                VisionRegistryReadStatus.Missing,
                "vision_registry_non_windows_default_disabled");
        }

        try
        {
            using var root = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine,
                RegistryView.Registry64);
            using var key = root.OpenSubKey(KeyPath, RegistryRights.ReadKey);
            if (key is null)
            {
                return new(
                    VisionRegistryReadStatus.Missing,
                    "vision_registry_key_missing");
            }

            if (!TryVerifyKeyAcl(key, out var aclCode))
                return new(VisionRegistryReadStatus.Invalid, aclCode);
            if (key.GetSubKeyNames().Length != 0)
            {
                return new(
                    VisionRegistryReadStatus.Invalid,
                    "vision_registry_subkey_forbidden");
            }

            var valueNames = key.GetValueNames();
            if (valueNames.Length == 0)
            {
                return new(
                    VisionRegistryReadStatus.Missing,
                    "vision_registry_state_missing");
            }
            if (valueNames.Length != 1 ||
                !string.Equals(valueNames[0], ValueName, StringComparison.Ordinal))
            {
                return new(
                    VisionRegistryReadStatus.Invalid,
                    "vision_registry_unknown_value");
            }
            if (key.GetValueKind(ValueName) != RegistryValueKind.String)
            {
                return new(
                    VisionRegistryReadStatus.Invalid,
                    "vision_registry_value_kind_invalid");
            }

            var value = key.GetValue(
                ValueName,
                null,
                RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            if (value is null || value.Length is <= 0 or > MaximumStateCharacters ||
                value.Contains('\0', StringComparison.Ordinal))
            {
                return new(
                    VisionRegistryReadStatus.Invalid,
                    "vision_registry_value_invalid");
            }

            return new(VisionRegistryReadStatus.Present, "present", value);
        }
        catch (Exception exception) when (exception is
                   UnauthorizedAccessException or
                   System.Security.SecurityException or
                   IOException)
        {
            return new(
                VisionRegistryReadStatus.Invalid,
                $"vision_registry_read_failed_{exception.GetType().Name}");
        }
    }

    /// <summary>
    /// Replaces the one REG_SZ state value. It never creates the key and writes
    /// only after the exact owner and DACL have been re-read from the same key
    /// handle.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static void WriteState(string state)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "The vision registry authority exists only on Windows.");
        if (string.IsNullOrEmpty(state) ||
            state.Length > MaximumStateCharacters ||
            state.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("Vision registry state is invalid.", nameof(state));
        }

        using var root = RegistryKey.OpenBaseKey(
            RegistryHive.LocalMachine,
            RegistryView.Registry64);
        using var key = root.OpenSubKey(KeyPath, CoreRights)
            ?? throw new InvalidOperationException(
                "The vision registry authority has not been provisioned by Setup.");
        if (!TryVerifyKeyAcl(key, out var aclCode))
            throw new UnauthorizedAccessException(
                $"The vision registry authority ACL is invalid ({aclCode}).");
        if (key.GetSubKeyNames().Length != 0 || key.GetValueNames().Any(
                name => !string.Equals(name, ValueName, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "The vision registry authority contains an unexpected entry.");
        }

        key.SetValue(ValueName, state, RegistryValueKind.String);
        key.Flush();
        if (key.GetValueKind(ValueName) != RegistryValueKind.String ||
            !string.Equals(
                key.GetValue(
                    ValueName,
                    null,
                    RegistryValueOptions.DoNotExpandEnvironmentNames) as string,
                state,
                StringComparison.Ordinal) ||
            !TryVerifyKeyAcl(key, out aclCode))
        {
            throw new IOException(
                $"The vision registry state could not be verified after write ({aclCode}).");
        }
    }

    public static bool VerifyProvisionedAcl(out string code)
    {
        code = "vision_registry_non_windows";
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var root = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine,
                RegistryView.Registry64);
            using var key = root.OpenSubKey(KeyPath, RegistryRights.ReadKey);
            if (key is null)
            {
                code = "vision_registry_key_missing";
                return false;
            }
            return TryVerifyKeyAcl(key, out code);
        }
        catch (Exception exception)
        {
            code = $"vision_registry_acl_read_failed_{exception.GetType().Name}";
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    internal static RegistrySecurity CreateExactSecurity()
    {
        var security = new RegistrySecurity();
        security.SetSecurityDescriptorSddlForm(
            $"O:SYD:P(A;;KA;;;SY)(A;;KA;;;BA)" +
            $"(A;;0x{(int)CoreRights:x};;;{CoreServiceIdentity.ServiceSid})" +
            "(A;;KR;;;BU)",
            AccessControlSections.Owner | AccessControlSections.Access);
        return security;
    }

    [SupportedOSPlatform("windows")]
    internal static bool VerifySecurityDescriptor(byte[] descriptor, out string code)
    {
        var raw = new RawSecurityDescriptor(descriptor, 0);
        if (raw.Owner?.Value != SystemSid)
        {
            code = "vision_registry_owner_invalid";
            return false;
        }
        if (!raw.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected))
        {
            code = "vision_registry_acl_not_protected";
            return false;
        }
        if (raw.DiscretionaryAcl is null || raw.DiscretionaryAcl.Count != 4)
        {
            code = "vision_registry_acl_rule_count_invalid";
            return false;
        }

        var expected = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [SystemSid] = (int)RegistryRights.FullControl,
            [AdministratorsSid] = (int)RegistryRights.FullControl,
            [CoreServiceIdentity.ServiceSid] = (int)CoreRights,
            [BuiltinUsersSid] = (int)RegistryRights.ReadKey,
        };
        foreach (GenericAce ace in raw.DiscretionaryAcl)
        {
            if (ace is not CommonAce common ||
                common.AceType != AceType.AccessAllowed ||
                common.AceFlags != AceFlags.None ||
                !expected.Remove(common.SecurityIdentifier.Value, out var rights) ||
                common.AccessMask != rights)
            {
                code = "vision_registry_acl_rule_invalid";
                return false;
            }
        }

        code = expected.Count == 0 ? "valid" : "vision_registry_acl_rule_missing";
        return expected.Count == 0;
    }

    [SupportedOSPlatform("windows")]
    private static bool TryVerifyKeyAcl(RegistryKey key, out string code)
    {
        try
        {
            var descriptor = key.GetAccessControl(
                    AccessControlSections.Owner | AccessControlSections.Access)
                .GetSecurityDescriptorBinaryForm();
            return VerifySecurityDescriptor(descriptor, out code);
        }
        catch (Exception exception)
        {
            code = $"vision_registry_acl_invalid_{exception.GetType().Name}";
            return false;
        }
    }
}
