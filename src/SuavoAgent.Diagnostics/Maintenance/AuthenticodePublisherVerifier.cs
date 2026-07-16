using System.Runtime.InteropServices;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SuavoAgent.Diagnostics.Maintenance;

public sealed record AuthenticodePublisherTrust(
    bool IsTrusted,
    string Code,
    string? Publisher)
{
    public static AuthenticodePublisherTrust Trusted(string publisher) =>
        new(true, "trusted", publisher);

    public static AuthenticodePublisherTrust Rejected(string code, string? publisher = null) =>
        new(false, code, publisher);
}

/// <summary>
/// Uses the native Windows trust provider to validate the complete Authenticode
/// policy, then pins the leaf signer to MKM's exact publisher identity. A valid
/// signature from another publisher is deliberately rejected.
/// </summary>
public static class AuthenticodePublisherVerifier
{
    public const string ExpectedPublisher = "MKM TECHNOLOGIES LLC";
    public const string SignerAllowlistMetadataKey =
        "SuavoAuthenticodeSignerSha256";
    private static readonly Guid GenericVerifyV2 =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public static AuthenticodePublisherTrust Verify(string executablePath)
    {
        if (!TryParseSignerAllowlist(
                ReadEmbeddedSignerAllowlist(),
                out var allowedSignerSha256))
            return AuthenticodePublisherTrust.Rejected(
                "authenticode_signer_allowlist_missing_or_invalid");
        return Verify(executablePath, allowedSignerSha256);
    }

    private static string? ReadEmbeddedSignerAllowlist() =>
        typeof(AuthenticodePublisherVerifier).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(attribute => string.Equals(
                attribute.Key,
                SignerAllowlistMetadataKey,
                StringComparison.Ordinal))
            ?.Value;

    internal static AuthenticodePublisherTrust Verify(
        string executablePath,
        IReadOnlySet<string> allowedSignerSha256)
        => VerifyCore(executablePath, ExpectedPublisher, allowedSignerSha256);

    /// <summary>
    /// Validates Windows Authenticode policy and pins the leaf certificate's
    /// exact publisher name. Callers must independently pin the executable's
    /// bytes when this overload is used for a third-party redistributable.
    /// </summary>
    public static AuthenticodePublisherTrust VerifyPublisher(
        string executablePath,
        string expectedPublisher)
    {
        if (string.IsNullOrWhiteSpace(expectedPublisher) ||
            expectedPublisher.Length > 256)
            return AuthenticodePublisherTrust.Rejected(
                "authenticode_expected_publisher_invalid");
        return VerifyCore(executablePath, expectedPublisher, allowedSignerSha256: null);
    }

    private static AuthenticodePublisherTrust VerifyCore(
        string executablePath,
        string expectedPublisher,
        IReadOnlySet<string>? allowedSignerSha256)
    {
        if (!OperatingSystem.IsWindows())
            return AuthenticodePublisherTrust.Rejected("authenticode_platform_unsupported");
        try
        {
            if (string.IsNullOrWhiteSpace(executablePath) ||
                !Path.IsPathFullyQualified(executablePath) ||
                !File.Exists(executablePath) ||
                (File.GetAttributes(executablePath) & FileAttributes.ReparsePoint) != 0)
                return AuthenticodePublisherTrust.Rejected("authenticode_file_invalid");

            using var fileInfo = new WinTrustFileInfo(executablePath);
            using var trustData = new WinTrustData(fileInfo);
            var nativeStatus = WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, trustData);
            if (nativeStatus != 0)
                return AuthenticodePublisherTrust.Rejected(
                    "authenticode_policy_invalid:" + nativeStatus.ToString("x8"));

            using var signed = X509Certificate.CreateFromSignedFile(executablePath);
            using var certificate = new X509Certificate2(signed);
            var publisher = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
            if (!string.Equals(publisher, expectedPublisher, StringComparison.Ordinal))
                return AuthenticodePublisherTrust.Rejected(
                    "authenticode_publisher_mismatch",
                    publisher);
            if (allowedSignerSha256 is not null &&
                !allowedSignerSha256.Contains(
                    certificate.GetCertHashString(HashAlgorithmName.SHA256)))
                return AuthenticodePublisherTrust.Rejected(
                    "authenticode_signer_not_allowlisted",
                    publisher);

            var codeSigning = certificate.Extensions
                .OfType<X509EnhancedKeyUsageExtension>()
                .Any(extension => extension.EnhancedKeyUsages
                    .Cast<Oid>()
                    .Any(oid => oid.Value == "1.3.6.1.5.5.7.3.3"));
            return codeSigning
                ? AuthenticodePublisherTrust.Trusted(publisher)
                : AuthenticodePublisherTrust.Rejected(
                    "authenticode_code_signing_eku_missing",
                    publisher);
        }
        catch (Exception ex) when (ex is
            IOException or
            UnauthorizedAccessException or
            System.Security.Cryptography.CryptographicException or
            ExternalException or
            ArgumentException)
        {
            return AuthenticodePublisherTrust.Rejected(
                "authenticode_unreadable:" + ex.GetType().Name);
        }
    }

    internal static bool TryParseSignerAllowlist(
        string? value,
        out IReadOnlySet<string> signerSha256)
    {
        var parsed = new HashSet<string>(StringComparer.Ordinal);
        signerSha256 = parsed;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4_096)
            return false;
        foreach (var candidate in value.Split(new[] { ',', ';' }, StringSplitOptions.None))
        {
            var normalized = candidate.Trim().ToUpperInvariant();
            if (normalized.Length != 64 ||
                normalized.Any(character => character is not (
                    >= '0' and <= '9' or >= 'A' and <= 'F')) ||
                !parsed.Add(normalized))
                return false;
        }
        signerSha256 = parsed;
        return parsed.Count is > 0 and <= 16;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(
        IntPtr hwnd,
        [MarshalAs(UnmanagedType.LPStruct)] Guid actionId,
        WinTrustData trustData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class WinTrustFileInfo : IDisposable
    {
        private readonly uint cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>();
        [MarshalAs(UnmanagedType.LPWStr)]
        private readonly string pcwszFilePath;
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
        private readonly uint dwUIChoice = 2; // WTD_UI_NONE
        private readonly uint fdwRevocationChecks = 0; // provider policy still validates chain/signature
        private readonly uint dwUnionChoice = 1; // WTD_CHOICE_FILE
        private IntPtr pFile;
        private readonly uint dwStateAction = 0;
        private readonly IntPtr hWVTStateData = IntPtr.Zero;
        private readonly IntPtr pwszURLReference = IntPtr.Zero;
        private readonly uint dwProvFlags = 0x00000080; // WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT
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
