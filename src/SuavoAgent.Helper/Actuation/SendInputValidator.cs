using System.ComponentModel;

namespace SuavoAgent.Helper.Actuation;

/// <summary>
/// Validates the return value of a Win32 <c>SendInput</c> call. The native
/// function returns the number of events actually inserted into the input
/// stream; anything less than <c>nInputs</c> means the OS blocked or hooked
/// the call (Win32 error 5 / ERROR_ACCESS_DENIED is the canonical Bug 22
/// signature — Helper holding an Identification-level token instead of an
/// Impersonation-level primary token). Silently discarding the return value
/// masked Bug 22 for weeks; this validator turns a silent partial-injection
/// into a thrown <see cref="Win32Exception"/> that the actuation entry
/// points already catch and surface as <c>ActuationResult.Reject</c> +
/// loud Serilog Warning.
///
/// Pure function, no Win32 dependency — runs on every platform CI builds on.
/// </summary>
internal static class SendInputValidator
{
    public static void EnsureFullyInjected(string verb, uint sent, int expected, int win32Error)
    {
        ArgumentException.ThrowIfNullOrEmpty(verb);
        ArgumentOutOfRangeException.ThrowIfNegative(expected);
        if (sent == (uint)expected) return;

        throw new Win32Exception(
            win32Error,
            $"SendInput failed for {verb}: injected {sent} of {expected} events (Win32 error 0x{win32Error:X8})");
    }
}
