using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SuavoAgent.Setup.Security;

internal sealed record PioneerRxExecutableEvidence(
    string ProcessName,
    string CanonicalExecutablePath,
    string ExecutableSha256,
    string AuthenticodeSignerSubject,
    string SignerCertificateSha256,
    string ProductName,
    string FileVersion);

/// <summary>
/// Captures all proposal executable fields while one read handle denies write/delete. Canonical
/// path, bytes, signer, and version therefore describe the same immutable file generation.
/// </summary>
internal static class PioneerRxExecutableEvidenceReader
{
    private static readonly Guid GenericVerifyV2 =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    internal static bool TryCapture(
        string executablePath,
        out PioneerRxExecutableEvidence? evidence,
        out string code)
    {
        evidence = null;
        code = "pioneerrx_executable_evidence_unavailable";
        if (!OperatingSystem.IsWindows())
        {
            code = "pioneerrx_executable_windows_required";
            return false;
        }
        try
        {
            if (string.IsNullOrWhiteSpace(executablePath) ||
                !Path.IsPathFullyQualified(executablePath))
                return false;
            using var stream = new FileStream(
                executablePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.SequentialScan);
            var canonical = CanonicalPath(stream.SafeFileHandle);
            if (canonical is null ||
                !string.Equals(
                    Path.GetFileName(canonical),
                    "PioneerPharmacy.exe",
                    StringComparison.OrdinalIgnoreCase))
            {
                code = "pioneerrx_executable_path_invalid";
                return false;
            }
            if (!VerifyAuthenticode(canonical))
            {
                code = "pioneerrx_authenticode_invalid";
                return false;
            }

            using var signed = X509Certificate.CreateFromSignedFile(canonical);
            using var certificate = new X509Certificate2(signed);
            var signerSubject = certificate.Subject;
            var signerDigest = LowerSha256(certificate.RawData);
            var version = FileVersionInfo.GetVersionInfo(canonical);
            var productName = version.ProductName ?? string.Empty;
            var fileVersion = version.FileVersion ?? string.Empty;
            if (string.IsNullOrWhiteSpace(signerSubject) ||
                string.IsNullOrWhiteSpace(productName) ||
                string.IsNullOrWhiteSpace(fileVersion))
            {
                code = "pioneerrx_executable_metadata_incomplete";
                return false;
            }

            stream.Position = 0;
            var executableDigest = LowerSha256(stream);
            evidence = new PioneerRxExecutableEvidence(
                Path.GetFileName(canonical),
                canonical,
                executableDigest,
                signerSubject,
                signerDigest,
                productName,
                fileVersion);
            code = "valid";
            return true;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or CryptographicException or
            ExternalException or ArgumentException)
        {
            evidence = null;
            return false;
        }
    }

    private static string? CanonicalPath(SafeFileHandle handle)
    {
        var capacity = 512;
        while (capacity <= 32768)
        {
            var buffer = new StringBuilder(capacity);
            var written = GetFinalPathNameByHandleW(
                handle,
                buffer,
                (uint)buffer.Capacity,
                0);
            if (written == 0) return null;
            if (written < buffer.Capacity)
            {
                var value = buffer.ToString();
                if (value.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ||
                    value.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase) ||
                    value.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase))
                    return null;
                if (value.StartsWith(@"\\?\", StringComparison.Ordinal)) value = value[4..];
                if (value.Length < 3 || !char.IsAsciiLetter(value[0]) || value[1] != ':' ||
                    value[2] is not ('\\' or '/'))
                    return null;
                return Path.GetFullPath(value);
            }
            capacity = checked((int)written + 1);
        }
        return null;
    }

    private static string LowerSha256(Stream stream)
    {
        var digest = SHA256.HashData(stream);
        try { return Convert.ToHexString(digest).ToLowerInvariant(); }
        finally { CryptographicOperations.ZeroMemory(digest); }
    }

    private static string LowerSha256(ReadOnlySpan<byte> bytes)
    {
        var digest = SHA256.HashData(bytes);
        try { return Convert.ToHexString(digest).ToLowerInvariant(); }
        finally { CryptographicOperations.ZeroMemory(digest); }
    }

    private static bool VerifyAuthenticode(string path)
    {
        using var fileInfo = new WinTrustFileInfo(path);
        using var trustData = new WinTrustData(fileInfo);
        return WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, trustData) == 0;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        StringBuilder path,
        uint pathLength,
        uint flags);

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(
        IntPtr hwnd,
        [MarshalAs(UnmanagedType.LPStruct)] Guid actionId,
        WinTrustData trustData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class WinTrustFileInfo : IDisposable
    {
        private readonly uint cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>();
        [MarshalAs(UnmanagedType.LPWStr)] private readonly string pcwszFilePath;
        private readonly IntPtr hFile = IntPtr.Zero;
        private readonly IntPtr pgKnownSubject = IntPtr.Zero;
        internal WinTrustFileInfo(string path) => pcwszFilePath = path;
        public void Dispose() { }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class WinTrustData : IDisposable
    {
        private readonly uint cbStruct = (uint)Marshal.SizeOf<WinTrustData>();
        private readonly IntPtr pPolicyCallbackData = IntPtr.Zero;
        private readonly IntPtr pSIPClientData = IntPtr.Zero;
        private readonly uint dwUIChoice = 2;
        private readonly uint fdwRevocationChecks = 0;
        private readonly uint dwUnionChoice = 1;
        private IntPtr pFile;
        private readonly uint dwStateAction = 0;
        private readonly IntPtr hWVTStateData = IntPtr.Zero;
        private readonly IntPtr pwszURLReference = IntPtr.Zero;
        private readonly uint dwProvFlags = 0x00000080;
        private readonly uint dwUIContext = 0;

        internal WinTrustData(WinTrustFileInfo file)
        {
            pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(file, pFile, fDeleteOld: false);
        }

        public void Dispose()
        {
            if (pFile == IntPtr.Zero) return;
            Marshal.DestroyStructure<WinTrustFileInfo>(pFile);
            Marshal.FreeHGlobal(pFile);
            pFile = IntPtr.Zero;
        }
    }
}
