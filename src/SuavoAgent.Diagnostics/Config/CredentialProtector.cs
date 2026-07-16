using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace SuavoAgent.Core.Config;

/// <summary>
/// Compatibility protector for SQL passwords that remain in immutable
/// appsettings. Setup seals them before staging; Core only verifies and reads.
/// Cloud authentication is stored separately in <see cref="DpapiCredentialStore"/>.
/// </summary>
public static class CredentialProtector
{
    private const string Prefix = "DPAPI:";

    public static bool IsProtected(string? value) =>
        string.IsNullOrEmpty(value) || value.StartsWith(Prefix, StringComparison.Ordinal);

    [SupportedOSPlatform("windows")]
    public static string? Protect(string? value)
    {
        if (string.IsNullOrEmpty(value) || IsProtected(value))
            return value;
        var encrypted = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(value),
            optionalEntropy: null,
            DataProtectionScope.LocalMachine);
        return Prefix + Convert.ToBase64String(encrypted);
    }

    [SupportedOSPlatform("windows")]
    public static string? Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value) || !value.StartsWith(Prefix, StringComparison.Ordinal))
            return value;

        try
        {
            var encrypted = Convert.FromBase64String(value[Prefix.Length..]);
            var clear = ProtectedData.Unprotect(
                encrypted,
                optionalEntropy: null,
                DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(clear);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            throw new InvalidDataException("A DPAPI-protected configuration secret is invalid.", ex);
        }
    }
}
