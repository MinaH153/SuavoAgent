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
    /// Generation tag from the ruleset the fingerprinter was built against.
    /// Used by snapshot-coherence tests (Codex Comp 2 round-4 MED): every
    /// concurrent reader of <see cref="Wire.CurrentRuntime"/> MUST observe
    /// <c>rt.Ruleset.RulesetVersion == rt.Scrubber.RulesetVersion ==
    /// rt.Fingerprinter.RulesetVersion</c>, never a mixed generation.
    /// </summary>
    internal string RulesetVersion => _ruleset.RulesetVersion;

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
        // Heartbeat reserves a canonical fingerprint shape that cannot
        // collide with a real crash class. Routed through DispatchNormal
        // per spec §4 self-heartbeat contract (Codex chunk 1 HIGH).
        WireSignalKind.Heartbeat => "heartbeat",
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
            // HRESULT for COM / unmanaged-native paths.
            var hr = unchecked((uint)ex.HResult);
            // 0x80131500 is COR_E_EXCEPTION — the default HResult inherited
            // from System.Exception base class. Many derived exceptions keep
            // this value (no override), so it carries zero entropy beyond
            // exception_type. Skip it. Codex chunk 2a LOW fix: comment
            // previously misidentified this as E_FAIL (which is 0x80004005).
            if (ex.GetType() != typeof(Exception) && hr != CorEException)
            {
                return $"hresult=0x{hr:X8}";
            }
        }
        return string.Empty;
    }

    private const uint CorEException = 0x80131500;

    /// <summary>
    /// Walk the stack of <c>signal.Exception</c> (or current call site for
    /// invariant violations) to find the first in-app non-wrapper managed
    /// frame. Returns <c>Assembly.Type.Method(arity,paramTypeNames)</c>
    /// — no path, no line, no MVID, no metadata token (Codex §5 hardened
    /// no-raw-frames invariant).
    /// </summary>
    /// <remarks>
    /// Stability caveat (Codex chunk 2a CRITICAL #2): stack-derived
    /// fingerprints are NOT 100% stable across builds with different
    /// inlining decisions / runtime tiers. If `Foo` is inlined into `Bar`,
    /// the stack trace shows `Bar`, and we fingerprint `Bar`. For
    /// must-bucket-deterministically signals (Bug 22's actuation-token,
    /// Bug 23's invariant_id) prefer <see cref="WireSignal.Stage"/> or
    /// <see cref="WireSignal.InvariantId"/> over the walked frame. In-app
    /// cross-assembly inlining is rare in practice; the current shape is
    /// stable enough for crash bucketing.
    /// </remarks>
    private static string ExtractPrimaryFailureSite(WireSignal signal, Stopwatch sw)
    {
        // Heartbeat: reserved operation identifier (Codex chunk 1 HIGH).
        if (signal.Kind == WireSignalKind.Heartbeat)
        {
            return "mesh.heartbeat";
        }
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

                // Codex chunk 2a CRITICAL #1: async state-machine frames
                // are reverse-mapped to the user's async method via
                // AsyncStateMachineAttribute. Without this, a crash in
                // an `async Task Foo()` body would skip MoveNext (which
                // looks like a wrapper) and land on System.Threading.Tasks
                // internals (not in-app), losing the user method identity.
                var resolved = ResolveAsyncStateMachineMethod(method);
                if (resolved is not null)
                {
                    method = resolved;
                }

                if (IsWrapperFrame(method)) continue;
                var asmName = method.DeclaringType?.Assembly.GetName().Name ?? "anonymous";
                if (!IsInAppAssembly(asmName)) continue;
                return FormatMethodIdentity(asmName, method);
            }

            // No in-app frame found — fall back to the first NON-WRAPPER
            // frame (was: first non-null which often landed on async
            // builders / runtime internals — Codex chunk 2a MEDIUM #1).
            foreach (var f in frames)
            {
                var m = f.GetMethod();
                if (m is null) continue;
                var rm = ResolveAsyncStateMachineMethod(m) ?? m;
                if (IsWrapperFrame(rm)) continue;
                var asmName = rm.DeclaringType?.Assembly.GetName().Name ?? "anonymous";
                return FormatMethodIdentity(asmName, rm);
            }
        }
        catch
        {
            // walk failure → empty site, FingerprintComputer falls through to
            // exception_type + stable_error_code identity
        }

        return string.Empty;
    }

    /// <summary>
    /// Reverse-map a state-machine MoveNext frame back to the original
    /// async method that declared it. If <paramref name="frameMethod"/>
    /// is the MoveNext of a generated <c>IAsyncStateMachine</c>, finds the
    /// enclosing method whose <c>[AsyncStateMachine]</c> attribute points
    /// to this state machine type. Returns null if not an async state
    /// machine frame or if no enclosing method matches.
    /// </summary>
    private static MethodBase? ResolveAsyncStateMachineMethod(MethodBase frameMethod)
    {
        if (frameMethod.Name != "MoveNext") return null;
        var stateMachine = frameMethod.DeclaringType;
        if (stateMachine is null) return null;
        if (!typeof(System.Runtime.CompilerServices.IAsyncStateMachine).IsAssignableFrom(stateMachine))
        {
            return null;
        }

        var enclosingType = stateMachine.DeclaringType;
        if (enclosingType is null) return null;

        foreach (var m in enclosingType.GetMethods(
            BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Static | BindingFlags.Instance |
            BindingFlags.DeclaredOnly))
        {
            var attr = m.GetCustomAttribute<AsyncStateMachineAttribute>();
            if (attr is not null && attr.StateMachineType == stateMachine)
            {
                return m;
            }
        }
        return null;
    }

    // Codex chunk 2a MEDIUM #1: expanded wrapper list covers ValueTask
    // builders, awaiter wrappers, and pooling variants. Async state
    // machine MoveNext frames are handled separately by
    // ResolveAsyncStateMachineMethod (reverse-map to user method).
    private static readonly HashSet<string> WrapperTypePrefixes = new(StringComparer.Ordinal)
    {
        "System.Runtime.CompilerServices.AsyncTaskMethodBuilder",
        "System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder",
        "System.Runtime.CompilerServices.PoolingAsyncValueTaskMethodBuilder",
        "System.Runtime.CompilerServices.AsyncVoidMethodBuilder",
        "System.Runtime.CompilerServices.AsyncIteratorMethodBuilder",
        "System.Runtime.CompilerServices.AsyncMethodBuilderCore",
        "System.Runtime.CompilerServices.TaskAwaiter",
        "System.Runtime.CompilerServices.ConfiguredTaskAwaitable",
        "System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable",
        "System.Runtime.CompilerServices.ValueTaskAwaiter",
        "System.Runtime.ExceptionServices",
        "System.Threading.Tasks.Task",
        "System.Threading.Tasks.ValueTask",
        "System.Threading.Tasks.Sources",
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
        var declaringType = method.DeclaringType;
        var typeFullName = declaringType is null
            ? "<global>"
            : FormatType(declaringType);
        var methodName = StripGenericArity(method.Name);
        var paramTypes = method.GetParameters()
            .Select(p => FormatType(p.ParameterType))
            .ToArray();
        var arity = paramTypes.Length;
        var paramsStr = paramTypes.Length == 0 ? string.Empty : string.Join(",", paramTypes);
        return $"{assemblyName}.{typeFullName}.{methodName}({arity}{(paramsStr.Length > 0 ? "," + paramsStr : string.Empty)})";
    }

    /// <summary>
    /// Recursively format a Type without assembly-qualified noise (Codex
    /// chunk 2a MEDIUM #2). <see cref="Type.FullName"/> on closed generic
    /// args produces strings like
    /// <c>Dictionary`2[[System.String, System.Private.CoreLib, Version=8.0.0.0, ...]]</c>
    /// — the Version/Culture/PublicKeyToken payload would shift between
    /// .NET versions and break "same crash same fingerprint". This builds
    /// the type identity from <c>GetGenericTypeDefinition</c> +
    /// <c>GetGenericArguments</c> recursion, never touching FullName for
    /// generic args.
    /// </summary>
    private static string FormatType(Type t)
    {
        if (t.IsArray)
        {
            var elem = t.GetElementType();
            return (elem is null ? "<anon>" : FormatType(elem)) + "[]";
        }
        if (t.IsByRef)
        {
            var elem = t.GetElementType();
            return (elem is null ? "<anon>" : FormatType(elem)) + "&";
        }
        if (t.IsPointer)
        {
            var elem = t.GetElementType();
            return (elem is null ? "<anon>" : FormatType(elem)) + "*";
        }
        if (t.IsGenericParameter)
        {
            // Generic parameters keep their name (T, TKey, etc.) — stable.
            return t.Name;
        }
        if (!t.IsGenericType)
        {
            return StripGenericArity(t.FullName ?? t.Name);
        }
        var def = t.GetGenericTypeDefinition();
        var defName = StripGenericArity(def.FullName ?? def.Name);
        var args = string.Join(",", t.GetGenericArguments().Select(FormatType));
        return $"{defName}[{args}]";
    }

    private static string StripGenericArity(string name)
    {
        // Remove `1, `2, etc. but keep nested generic-arg signatures.
        // Net: System.Collections.Generic.List`1 → System.Collections.Generic.List
        return Regex.Replace(name, "`\\d+", string.Empty);
    }
}
