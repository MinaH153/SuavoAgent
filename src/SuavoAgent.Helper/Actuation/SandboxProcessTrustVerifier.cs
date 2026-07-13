using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SuavoAgent.Contracts.Ipc;

namespace SuavoAgent.Helper.Actuation;

/// <summary>
/// Authenticates the real process behind a resolved sandbox PID. A declared
/// process-name match is not identity: an interactive user can run a renamed
/// <c>notepad.exe</c> from a writable directory. Live sandbox input therefore
/// requires all of: expected process/packaged alias, canonical Windows-owned
/// location, valid Authenticode signature, Microsoft publisher, and no
/// protected PMS/PHI identity in the resolved image metadata.
/// </summary>
internal static class SandboxProcessTrustVerifier
{
    internal readonly record struct Verdict(bool Trusted, string Code, string? ImagePath = null)
    {
        public static Verdict Allow(string path) => new(true, "trusted", path);
        public static Verdict Deny(string code, string? path = null) => new(false, code, path);
    }

    public static Verdict VerifyResolvedProcess(int pid, string requestedProcess)
    {
        if (!OperatingSystem.IsWindows() || pid <= 0 || string.IsNullOrWhiteSpace(requestedProcess))
            return Verdict.Deny("identity_unavailable");

        var imagePath = SuavoAgent.Helper.ProcessImageInterop.Get((uint)pid, out _);
        if (string.IsNullOrWhiteSpace(imagePath))
            return Verdict.Deny("image_path_unavailable");

        string resolvedName;
        try
        {
            using var process = Process.GetProcessById(pid);
            resolvedName = process.ProcessName;
        }
        catch
        {
            return Verdict.Deny("process_name_unavailable", imagePath);
        }

        return VerifyImageIdentity(requestedProcess, resolvedName, imagePath);
    }

    public static Verdict VerifyExecutablePath(string path, string requestedProcess)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(path))
            return Verdict.Deny("identity_unavailable");
        return VerifyImageIdentity(
            requestedProcess,
            Path.GetFileNameWithoutExtension(path),
            path);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static Verdict VerifyImageIdentity(
        string requestedProcess,
        string resolvedName,
        string imagePath)
    {
        var canonicalPath = CanonicalizeExistingFile(imagePath);
        if (canonicalPath is null)
            return Verdict.Deny("canonical_path_unavailable", imagePath);
        imagePath = canonicalPath;

        FileVersionInfo version;
        try { version = FileVersionInfo.GetVersionInfo(imagePath); }
        catch { return Verdict.Deny("version_metadata_unavailable", imagePath); }

        if (ProtectedDesktopProcessClassifier.IsProtectedIdentity(
                requestedProcess,
                resolvedName,
                imagePath,
                version.ProductName,
                version.FileDescription,
                version.CompanyName))
        {
            return Verdict.Deny("protected_process", imagePath);
        }

        if (string.IsNullOrWhiteSpace(version.OriginalFilename))
            return Verdict.Deny("original_filename_unavailable", imagePath);

        if (!IdentityMatchesRequested(
                requestedProcess,
                resolvedName,
                imagePath,
                version.OriginalFilename,
                version.InternalName))
            return Verdict.Deny("resolved_process_mismatch", imagePath);

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!IsTrustedWindowsLocation(imagePath, windows, programFiles))
            return Verdict.Deny("untrusted_image_location", imagePath);

        if (!VerifyAuthenticode(imagePath))
            return Verdict.Deny("authenticode_invalid", imagePath);

        string signerSubject;
        try
        {
            using var signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(imagePath));
            signerSubject = signer.Subject;
        }
        catch
        {
            return Verdict.Deny("signer_unavailable", imagePath);
        }

        if (!IsMicrosoftSignerSubject(signerSubject))
            return Verdict.Deny("publisher_not_microsoft", imagePath);

        return Verdict.Allow(imagePath);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static string? CanonicalizeExistingFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            using var handle = File.OpenHandle(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return CanonicalizeExistingFile(handle);
        }
        catch
        {
            // Any sharing, access, reparse-resolution, or path error is a
            // terminal identity failure. Never fall back to the lexical path.
        }
        return null;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static string? CanonicalizeExistingFile(SafeFileHandle handle)
    {
        if (handle is null || handle.IsInvalid || handle.IsClosed) return null;
        try
        {
            var capacity = 512;
            while (capacity <= 32768)
            {
                var buffer = new StringBuilder(capacity);
                var written = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
                if (written == 0) return null;
                if (written < buffer.Capacity)
                {
                    var normalized = NormalizeFinalDosPath(buffer.ToString());
                    return normalized is null ? null : Path.GetFullPath(normalized);
                }
                capacity = checked((int)written + 1);
            }
        }
        catch
        {
            return null;
        }
        return null;
    }

    internal static string? NormalizeFinalDosPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var value = path.Trim();

        // GetFinalPathNameByHandle normally returns \\?\C:\... . Network,
        // device and volume-GUID paths are not valid local sandbox identities.
        if (value.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase))
            return null;
        if (value.StartsWith(@"\\?\", StringComparison.Ordinal))
            value = value[4..];
        if (value.StartsWith(@"\\", StringComparison.Ordinal) ||
            value.Length < 3 ||
            !char.IsAsciiLetter(value[0]) ||
            value[1] != ':' ||
            (value[2] != '\\' && value[2] != '/'))
            return null;
        return value;
    }

    internal static bool IdentityMatchesRequested(
        string requestedProcess,
        string resolvedProcessName,
        string imagePath,
        string? originalFilename = null,
        string? internalName = null)
    {
        var expected = PackagedAppAliases.CandidateProcessNames(requestedProcess)
            .Select(ProtectedDesktopProcessClassifier.CanonicalProcessStem)
            .Where(x => x.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (expected.Count == 0) return false;

        var resolved = ProtectedDesktopProcessClassifier.CanonicalProcessStem(resolvedProcessName);
        var image = ProtectedDesktopProcessClassifier.CanonicalProcessStem(imagePath);
        if (!expected.Contains(resolved) || !expected.Contains(image)) return false;

        // Authenticode authenticates bytes/publisher, not the filename chosen by
        // the copier. Pin the PE version resource too so a genuine Microsoft
        // binary such as mshta.exe cannot be copied/renamed to notepad.exe and
        // inherit Notepad's sandbox authority.
        if (originalFilename is not null)
        {
            var original = ProtectedDesktopProcessClassifier.CanonicalProcessStem(originalFilename);
            if (original.Length == 0 || !expected.Contains(original)) return false;
        }
        if (!string.IsNullOrWhiteSpace(internalName))
        {
            var internalStem = ProtectedDesktopProcessClassifier.CanonicalProcessStem(internalName);
            if (internalStem.Length > 0 && !expected.Contains(internalStem)) return false;
        }
        return true;
    }

    internal static bool IsTrustedWindowsLocation(
        string imagePath,
        string windowsDirectory,
        string programFilesDirectory)
    {
        if (string.IsNullOrWhiteSpace(imagePath) ||
            string.IsNullOrWhiteSpace(windowsDirectory) ||
            string.IsNullOrWhiteSpace(programFilesDirectory))
        {
            return false;
        }

        try
        {
            var full = Path.GetFullPath(imagePath);
            var windowsRoot = Path.GetFullPath(windowsDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var parent = Path.GetDirectoryName(full)?.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            var system32 = Path.Combine(windowsRoot, "System32")
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var sysWow64 = Path.Combine(windowsRoot, "SysWOW64")
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var windowsApps = Path.Combine(Path.GetFullPath(programFilesDirectory), "WindowsApps")
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return string.Equals(parent, windowsRoot, StringComparison.OrdinalIgnoreCase) ||
                   full.StartsWith(system32, StringComparison.OrdinalIgnoreCase) ||
                   full.StartsWith(sysWow64, StringComparison.OrdinalIgnoreCase) ||
                   full.StartsWith(windowsApps, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsMicrosoftSignerSubject(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject)) return false;

        // Match the organization RDN exactly. Substring/CN checks would accept a
        // different publisher such as "Microsoft Tools LLC" or an OU containing
        // the text. Microsoft-signed Windows binaries carry O=Microsoft Corporation.
        return subject.Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(component => component.Trim())
            .Any(component => string.Equals(
                component,
                "O=Microsoft Corporation",
                StringComparison.OrdinalIgnoreCase));
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static bool VerifyAuthenticode(string path)
    {
        var fileInfo = new WinTrustFileInfo(path);
        var fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);
            var data = new WinTrustData(fileInfoPtr);
            var action = WinTrustActionGenericVerifyV2;
            return WinVerifyTrust(IntPtr.Zero, ref action, ref data) == 0;
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(fileInfoPtr);
            Marshal.FreeHGlobal(fileInfoPtr);
        }
    }

    internal static bool TryReadSignerEvidence(
        string path,
        out string subject,
        out string certificateSha256)
    {
        subject = string.Empty;
        certificateSha256 = string.Empty;
        try
        {
            using var signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            subject = signer.Subject;
            certificateSha256 = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(signer.RawData)).ToLowerInvariant();
            return !string.IsNullOrWhiteSpace(subject) && certificateSha256.Length == 64;
        }
        catch
        {
            return false;
        }
    }

    private static readonly Guid WinTrustActionGenericVerifyV2 =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint StructSize;
        [MarshalAs(UnmanagedType.LPWStr)] public string FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;

        public WinTrustFileInfo(string path)
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>();
            FilePath = path;
            FileHandle = IntPtr.Zero;
            KnownSubject = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;

        public WinTrustData(IntPtr fileInfo)
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustData>();
            PolicyCallbackData = IntPtr.Zero;
            SipClientData = IntPtr.Zero;
            UiChoice = 2;             // WTD_UI_NONE
            RevocationChecks = 0;     // WTD_REVOKE_NONE (offline-safe)
            UnionChoice = 1;          // WTD_CHOICE_FILE
            FileInfo = fileInfo;
            StateAction = 0;          // WTD_STATEACTION_IGNORE
            StateData = IntPtr.Zero;
            UrlReference = IntPtr.Zero;
            ProviderFlags = 0x00001000; // WTD_CACHE_ONLY_URL_RETRIEVAL
            UiContext = 0;
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int WinVerifyTrust(
        IntPtr hwnd,
        ref Guid actionId,
        ref WinTrustData trustData);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        [Out] StringBuilder filePath,
        uint filePathLength,
        uint flags);
}
