using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using SuavoAgent.Helper.Actuation;

namespace SuavoAgent.Helper.SystemObservers.BrowserConnector;

internal interface IWindowsBrowserProcessTrustSystem
{
    bool IsSupportedPlatform { get; }

    bool TryGetCanonicalProcessPath(uint processId, out string canonicalPath);

    bool TryCanonicalizeExistingPath(string path, out string canonicalPath);

    bool IsExpectedMachineVendorPath(string path, BrowserFamily browser);

    bool HasNoReparseAncestor(string path);

    bool HasProtectedAclChain(string path, BrowserFamily browser);

    bool HasExpectedFileIdentity(string path, BrowserFamily browser);
}

/// <summary>
/// Shared exact browser-image proof for both native-channel authority and
/// parent/window corroboration. The signed per-device executable path is part
/// of identity; a copied genuine browser binary receives no authority.
/// </summary>
internal static class WindowsBrowserProcessTrustVerifier
{
    public static bool Verify(
        uint processId,
        BrowserConnectorAuthorityEntry authorization) =>
        Verify(processId, authorization, new WindowsBrowserProcessTrustSystem());

    internal static bool Verify(
        uint processId,
        BrowserConnectorAuthorityEntry authorization,
        IWindowsBrowserProcessTrustSystem system)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(system);
        if (processId == 0 ||
            !system.IsSupportedPlatform ||
            !BrowserExecutablePathPolicy.IsValidAuthorityPath(
                authorization.BrowserExecutablePath,
                authorization.Browser) ||
            !system.TryGetCanonicalProcessPath(processId, out var processPath) ||
            !system.TryCanonicalizeExistingPath(
                authorization.BrowserExecutablePath,
                out var authorizedPath) ||
            !string.Equals(
                authorizedPath,
                authorization.BrowserExecutablePath,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                processPath,
                authorizedPath,
                StringComparison.OrdinalIgnoreCase) ||
            !system.IsExpectedMachineVendorPath(processPath, authorization.Browser) ||
            !system.HasNoReparseAncestor(processPath) ||
            !system.HasProtectedAclChain(processPath, authorization.Browser) ||
            !system.HasExpectedFileIdentity(processPath, authorization.Browser))
        {
            return false;
        }

        return true;
    }

    internal static bool IsExpectedPublisherSubject(
        BrowserFamily browser,
        string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
            return false;
        var expectedOrganization = browser switch
        {
            BrowserFamily.Chrome => "Google LLC",
            BrowserFamily.Edge => "Microsoft Corporation",
            _ => string.Empty,
        };
        if (expectedOrganization.Length == 0)
            return false;

        return subject.Split(
                new[] { ',', ';', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries)
            .Select(component => component.Trim())
            .Any(component => string.Equals(
                component,
                $"O={expectedOrganization}",
                StringComparison.OrdinalIgnoreCase));
    }
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsBrowserProcessTrustSystem : IWindowsBrowserProcessTrustSystem
{
    public bool IsSupportedPlatform => OperatingSystem.IsWindows();

    public bool TryGetCanonicalProcessPath(
        uint processId,
        out string canonicalPath)
    {
        canonicalPath = string.Empty;
        if (!OperatingSystem.IsWindows())
            return false;
        var rawPath = ProcessImageInterop.Get(processId, out _);
        if (string.IsNullOrWhiteSpace(rawPath))
            return false;
        return TryCanonicalizeExistingPath(rawPath, out canonicalPath);
    }

    public bool TryCanonicalizeExistingPath(
        string path,
        out string canonicalPath)
    {
        canonicalPath = string.Empty;
        if (!OperatingSystem.IsWindows())
            return false;
        var canonical = SandboxProcessTrustVerifier.CanonicalizeExistingFile(path);
        if (string.IsNullOrWhiteSpace(canonical))
            return false;
        canonicalPath = canonical;
        return true;
    }

    public bool IsExpectedMachineVendorPath(
        string path,
        BrowserFamily browser) =>
        OperatingSystem.IsWindows() &&
        BrowserExecutablePathPolicy.IsUnderExpectedMachineVendorPath(
            path,
            browser,
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));

    public bool HasNoReparseAncestor(string path)
    {
        if (!OperatingSystem.IsWindows())
            return false;
        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root))
                return false;

            var current = root;
            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                return false;
            foreach (var segment in fullPath[root.Length..]
                         .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                    return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool HasProtectedAclChain(string path, BrowserFamily browser)
    {
        if (!OperatingSystem.IsWindows() ||
            !TryGetMachineRootForVendorPath(path, browser, out var machineRoot))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            var relative = Path.GetRelativePath(machineRoot, fullPath);
            if (relative is "." or ".." ||
                relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                return false;
            }

            var evidence = new List<BrowserAclObjectEvidence>();
            if (!TryCaptureAclEvidence(
                    new DirectoryInfo(machineRoot).GetAccessControl(
                        AccessControlSections.Access | AccessControlSections.Owner),
                    BrowserAclObjectKind.Directory,
                    depth: 0,
                    out var rootEvidence))
            {
                return false;
            }
            evidence.Add(rootEvidence);

            var current = machineRoot;
            var segments = relative.Split(
                Path.DirectorySeparatorChar,
                StringSplitOptions.RemoveEmptyEntries);
            for (var index = 0; index < segments.Length; index++)
            {
                current = Path.Combine(current, segments[index]);
                var isFile = index == segments.Length - 1;
                FileSystemSecurity security = isFile
                    ? new FileInfo(current).GetAccessControl(
                        AccessControlSections.Access | AccessControlSections.Owner)
                    : new DirectoryInfo(current).GetAccessControl(
                        AccessControlSections.Access | AccessControlSections.Owner);
                if (!TryCaptureAclEvidence(
                        security,
                        isFile ? BrowserAclObjectKind.File : BrowserAclObjectKind.Directory,
                        depth: index + 1,
                        out var objectEvidence))
                {
                    return false;
                }
                evidence.Add(objectEvidence);
            }
            return segments.Length > 0 &&
                   BrowserExecutableAclPolicy.IsProtectedChain(evidence);
        }
        catch
        {
            return false;
        }
    }

    public bool HasExpectedFileIdentity(string path, BrowserFamily browser)
    {
        if (!OperatingSystem.IsWindows())
            return false;
        var expectedFileName = BrowserExecutablePathPolicy.ExpectedFileName(browser);
        if (expectedFileName.Length == 0 ||
            !string.Equals(
                Path.GetFileName(path),
                expectedFileName,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        FileVersionInfo version;
        try
        {
            version = FileVersionInfo.GetVersionInfo(path);
        }
        catch
        {
            return false;
        }

        return string.Equals(
                   version.OriginalFilename,
                   expectedFileName,
                   StringComparison.OrdinalIgnoreCase) &&
               SandboxProcessTrustVerifier.VerifyAuthenticode(path) &&
               SandboxProcessTrustVerifier.TryReadSignerEvidence(
                   path,
                   out var subject,
                   out _) &&
               WindowsBrowserProcessTrustVerifier.IsExpectedPublisherSubject(
                   browser,
                   subject);
    }

    private static bool TryGetMachineRootForVendorPath(
        string path,
        BrowserFamily browser,
        out string machineRoot)
    {
        machineRoot = string.Empty;
        var suffix = browser switch
        {
            BrowserFamily.Chrome => Path.Combine("Google", "Chrome", "Application"),
            BrowserFamily.Edge => Path.Combine("Microsoft", "Edge", "Application"),
            _ => string.Empty,
        };
        if (suffix.Length == 0)
            return false;

        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 })
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;
            var exactMachineRoot = Path.GetFullPath(root);
            var candidate = Path.GetFullPath(Path.Combine(exactMachineRoot, suffix));
            if (path.StartsWith(
                    candidate.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                machineRoot = exactMachineRoot;
                return true;
            }
        }
        return false;
    }

    private static bool TryCaptureAclEvidence(
        FileSystemSecurity security,
        BrowserAclObjectKind kind,
        int depth,
        out BrowserAclObjectEvidence evidence)
    {
        evidence = default!;
        var descriptor = new RawSecurityDescriptor(
            security.GetSecurityDescriptorBinaryForm(),
            0);
        if (descriptor.Owner is null ||
            descriptor.DiscretionaryAcl is null ||
            descriptor.DiscretionaryAcl.Count > 1_024)
        {
            return false;
        }

        var rules = new List<BrowserAclRuleEvidence>(
            descriptor.DiscretionaryAcl.Count);
        foreach (GenericAce ace in descriptor.DiscretionaryAcl)
        {
            var flags = ace.AceFlags;
            var known = ace as KnownAce;
            var qualified = ace as QualifiedAce;
            var effect = qualified?.AceQualifier switch
            {
                AceQualifier.AccessDenied => BrowserAclRuleEffect.Deny,
                AceQualifier.AccessAllowed when !qualified.IsCallback =>
                    BrowserAclRuleEffect.Allow,
                _ => BrowserAclRuleEffect.Unknown,
            };
            rules.Add(new BrowserAclRuleEvidence(
                known?.SecurityIdentifier?.Value ?? string.Empty,
                effect,
                known is null ? uint.MaxValue : unchecked((uint)known.AccessMask),
                flags.HasFlag(AceFlags.Inherited),
                flags.HasFlag(AceFlags.ContainerInherit),
                flags.HasFlag(AceFlags.ObjectInherit),
                flags.HasFlag(AceFlags.InheritOnly),
                flags.HasFlag(AceFlags.NoPropagateInherit)));
        }

        evidence = new BrowserAclObjectEvidence(
            descriptor.Owner.Value,
            kind,
            depth,
            rules);
        return true;
    }
}
