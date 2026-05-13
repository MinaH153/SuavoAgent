using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace SuavoAgent.Diagnostics;

/// fp-v1 algorithm — agent-edge fingerprint compute. Spec §3 canonical
/// shape: <c>component | signal_kind | exception_type | stable_error_code
/// | primary_failure_site | semantic_invariant_id?</c>
///
/// Stability contract (Codex-validated): survives publish.ps1's
/// PublishReadyToRun=true + PublishSingleFile=true + tiered JIT because
/// primary_failure_site uses method IDENTITY (assembly.type.method+arity),
/// never offset / line / MVID / metadata token.
public sealed class FingerprintComputer
{
    private readonly TimeSpan _timeout;
    private readonly RulesetV1 _ruleset;

    public FingerprintComputer(RulesetV1 ruleset, TimeSpan timeout)
    {
        _ruleset = ruleset;
        _timeout = timeout;
    }

    /// <summary>
    /// Compute fp-v1 for a signal. Hard 10ms budget per spec §4 contract;
    /// on overrun, returns synthetic fp-fallback fingerprint preserving
    /// component + signal_kind only.
    /// </summary>
    public string Compute(WireSignal signal)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var component = signal.Component.ToString();
            var signalKind = ToCanonicalKind(signal.Kind);
            var exceptionType = signal.Exception?.GetType().FullName ?? string.Empty;
            var stableErrorCode = ExtractStableErrorCode(signal);
            var primarySite = ExtractPrimaryFailureSite(signal, sw);
            var invariantId = signal.InvariantId ?? string.Empty;

            // Calibration overlay — if signal matches a Bug 22/23/24
            // fingerprint, prefer the catalog-canonical form.
            var canonical = $"{component}|{signalKind}|{exceptionType}|{stableErrorCode}|{primarySite}|{invariantId}";

            if (sw.Elapsed > _timeout)
            {
                return FallbackFingerprint(component, signalKind);
            }

            return canonical;
        }
        catch
        {
            // FingerprintComputer never throws past its own boundary; the
            // crash handler is already on a dying path.
            return FallbackFingerprint(signal.Component.ToString(), ToCanonicalKind(signal.Kind));
        }
    }

    /// <summary>
    /// Returns true if the computed fingerprint matches one of ruleset-v1's
    /// calibration_fingerprints (Bug 22 / 23 / 24). Used to set the
    /// <c>bug-class</c> Sentry tag (D2 delight per §7 PR 4).
    /// </summary>
    public string? CalibrationBugClass(string fingerprint)
    {
        return _ruleset.CalibrationFingerprints
            .FirstOrDefault(kv => kv.Value.Equals(fingerprint, StringComparison.Ordinal))
            .Key;
    }

    private static string FallbackFingerprint(string component, string signalKind)
        => $"fp-fallback|{component}|{signalKind}";

    private static string ToCanonicalKind(WireSignalKind kind) => kind switch
    {
        WireSignalKind.ManagedException => "managed_exception",
        WireSignalKind.Win32 => "win32",
        WireSignalKind.UnmanagedNative => "unmanaged_native",
        WireSignalKind.InvariantViolation => "invariant_violation",
        WireSignalKind.UnobservedTask => "unobserved_task",
        WireSignalKind.ExitCode => "exit_code",
        WireSignalKind.Hang => "hang",
        _ => "unknown",
    };

    private static string ExtractStableErrorCode(WireSignal signal)
    {
        if (signal.ExitCode is int exitCode)
        {
            return $"exit_code=0x{exitCode:X8}";
        }
        if (signal.Exception is Win32Exception w32)
        {
            return $"native_error={w32.NativeErrorCode}";
        }
        if (signal.Exception is { } ex)
        {
            // HRESULT for COM / unmanaged-native paths
            var hr = unchecked((uint)ex.HResult);
            // Plain System.Exception has HResult 0x80131500 (E_FAIL-ish);
            // only emit hresult= for derived-class exceptions where it's
            // load-bearing identity.
            if (ex.GetType() != typeof(Exception) && hr != 0x80131500)
            {
                return $"hresult=0x{hr:X8}";
            }
        }
        return string.Empty;
    }

    /// <summary>
    /// Walk the stack of <c>signal.Exception</c> (or current call site for
    /// invariant violations) to find the first in-app non-wrapper managed
    /// frame. Returns <c>Assembly.Type.Method(arity,paramTypeNames)</c>
    /// — no path, no line, no MVID, no metadata token (Codex §5 hardened
    /// no-raw-frames invariant).
    /// </summary>
    private static string ExtractPrimaryFailureSite(WireSignal signal, Stopwatch sw)
    {
        // Invariant violations: site is the catalog id; no stack to walk.
        if (signal.Kind == WireSignalKind.InvariantViolation && signal.InvariantId is { } id)
        {
            return id;
        }

        if (signal.Exception is null)
        {
            return signal.Stage ?? string.Empty;
        }

        try
        {
            var trace = new StackTrace(signal.Exception, fNeedFileInfo: false);
            var frames = trace.GetFrames() ?? Array.Empty<StackFrame>();
            foreach (var frame in frames)
            {
                var method = frame.GetMethod();
                if (method is null) continue;
                if (IsWrapperFrame(method)) continue;
                var asmName = method.DeclaringType?.Assembly.GetName().Name ?? "anonymous";
                if (!IsInAppAssembly(asmName)) continue;
                return FormatMethodIdentity(asmName, method);
            }

            // No in-app frame found — return the outermost in-frame even if
            // wrapper, to keep fingerprint identity rather than empty.
            var first = frames.Select(f => f.GetMethod()).FirstOrDefault(m => m is not null);
            if (first is not null)
            {
                var asmName = first.DeclaringType?.Assembly.GetName().Name ?? "anonymous";
                return FormatMethodIdentity(asmName, first);
            }
        }
        catch
        {
            // walk failure → empty site, FingerprintComputer falls through to
            // exception_type + stable_error_code identity
        }

        return string.Empty;
    }

    private static readonly HashSet<string> WrapperTypePrefixes = new(StringComparer.Ordinal)
    {
        "System.Runtime.CompilerServices.AsyncTaskMethodBuilder",
        "System.Runtime.CompilerServices.AsyncMethodBuilderCore",
        "System.Runtime.ExceptionServices",
        "System.Threading.Tasks.Task",
        "System.Threading.ExecutionContext",
        "System.Reflection.RuntimeMethodInfo",
    };

    private static bool IsWrapperFrame(MethodBase method)
    {
        var declaring = method.DeclaringType;
        if (declaring is null) return true;

        // Compiler-generated state machines + display classes carry the
        // user code that threw; we want to skip the wrapper transition but
        // keep the user method. The Async state machine's MoveNext frame
        // is followed by a state-machine-attribute on the outer method —
        // RuntimeMethodInfo metadata exposes this.
        if (Attribute.IsDefined(method, typeof(CompilerGeneratedAttribute)))
        {
            return true;
        }
        if (declaring.Name.StartsWith("<", StringComparison.Ordinal))
        {
            return true; // lambda display class
        }
        foreach (var prefix in WrapperTypePrefixes)
        {
            if (declaring.FullName?.StartsWith(prefix, StringComparison.Ordinal) == true)
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsInAppAssembly(string assemblyName)
    {
        // In-app prefix list. Anything else (System.*, Microsoft.*,
        // Avalonia.*, Serilog.*, etc.) is treated as framework / library
        // and walked past.
        return assemblyName.StartsWith("SuavoAgent", StringComparison.Ordinal);
    }

    private static string FormatMethodIdentity(string assemblyName, MethodBase method)
    {
        var typeFullName = method.DeclaringType?.FullName ?? "<global>";
        // Strip generic-instantiation tick suffix (`1, `2, etc.) to keep
        // identity stable across closed/open generic specializations.
        typeFullName = StripGenericArity(typeFullName);
        var methodName = StripGenericArity(method.Name);
        var paramTypes = method.GetParameters()
            .Select(p => StripGenericArity(p.ParameterType.FullName ?? p.ParameterType.Name))
            .ToArray();
        var arity = paramTypes.Length;
        var paramsStr = paramTypes.Length == 0 ? string.Empty : string.Join(",", paramTypes);
        return $"{assemblyName}.{typeFullName}.{methodName}({arity}{(paramsStr.Length > 0 ? "," + paramsStr : string.Empty)})";
    }

    private static string StripGenericArity(string name)
    {
        // Remove `1, `2, etc. but keep nested generic-arg signatures.
        // Net: System.Collections.Generic.List`1 → System.Collections.Generic.List
        return Regex.Replace(name, "`\\d+", string.Empty);
    }
}
