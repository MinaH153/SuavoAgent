using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA2;
using Serilog;

namespace SuavoAgent.Helper.SystemObservers;

internal sealed record WindowStructureSnapshot(
    bool Success,
    string? TreeHash,
    int ElementCount,
    bool Truncated,
    string? FailureCode,
    long WindowHandle = 0,
    int ProcessId = 0);

internal enum WindowStructureCaptureProfile
{
    MultiApp,
    Pms,
}

internal interface IWindowStructureSnapshotProvider
{
    WindowStructureSnapshot Capture(
        nint windowHandle,
        int? expectedProcessId = null,
        WindowStructureCaptureProfile profile = WindowStructureCaptureProfile.MultiApp);
}

internal interface IWindowProcessIdentityResolver
{
    bool TryGetProcessId(nint windowHandle, out int processId);
}

internal sealed class Win32WindowProcessIdentityResolver : IWindowProcessIdentityResolver
{
    public bool TryGetProcessId(nint windowHandle, out int processId)
    {
        processId = 0;
        if (!OperatingSystem.IsWindows() || windowHandle == 0) return false;
        _ = GetWindowThreadProcessId(windowHandle, out var rawProcessId);
        if (rawProcessId is 0 or > int.MaxValue) return false;
        processId = (int)rawProcessId;
        return true;
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);
}

/// <summary>
/// Parent-side provider. Every COM/UIA walk runs in a child Helper process so
/// a provider that ignores UIA timeouts can be terminated without poisoning
/// the long-lived interactive Helper.
/// </summary>
internal sealed class IsolatedWindowStructureSnapshotProvider : IWindowStructureSnapshotProvider
{
    internal static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(3);

    private readonly IWindowSnapshotWorkerProcess _worker;
    private readonly string _executablePath;
    private readonly TimeSpan _timeout;
    private readonly bool _requireWindows;
    private readonly IWindowProcessIdentityResolver? _processIdentity;

    internal IsolatedWindowStructureSnapshotProvider()
        : this(
            new SystemWindowSnapshotWorkerProcess(),
            Environment.ProcessPath ?? string.Empty,
            CaptureTimeout,
            requireWindows: true,
            processIdentity: new Win32WindowProcessIdentityResolver())
    {
    }

    internal IsolatedWindowStructureSnapshotProvider(
        IWindowSnapshotWorkerProcess worker,
        string executablePath,
        TimeSpan timeout,
        bool requireWindows,
        IWindowProcessIdentityResolver? processIdentity = null)
    {
        _worker = worker;
        _executablePath = executablePath;
        _timeout = timeout;
        _requireWindows = requireWindows;
        _processIdentity = processIdentity;
    }

    public WindowStructureSnapshot Capture(
        nint windowHandle,
        int? expectedProcessId = null,
        WindowStructureCaptureProfile profile = WindowStructureCaptureProfile.MultiApp)
    {
        if (_requireWindows && !OperatingSystem.IsWindows())
            return Failure("unsupported_platform");
        if (windowHandle == 0)
            return Failure("invalid_window");
        if (string.IsNullOrWhiteSpace(_executablePath))
            return Failure("worker_executable_unavailable");

        var boundProcessId = expectedProcessId.GetValueOrDefault();
        if (boundProcessId <= 0)
        {
            if (_processIdentity is null
                || !_processIdentity.TryGetProcessId(windowHandle, out boundProcessId))
            {
                return Failure("window_process_unavailable");
            }
        }
        else if (_processIdentity is not null
                 && (!_processIdentity.TryGetProcessId(windowHandle, out var actualProcessId)
                     || actualProcessId != boundProcessId))
        {
            return Failure("window_process_mismatch");
        }

        var execution = _worker.Execute(
            _executablePath,
            windowHandle,
            boundProcessId,
            profile,
            _timeout);
        if (execution.TimedOut)
        {
            return Failure(execution.Terminated
                ? "provider_timeout"
                : "provider_timeout_unterminated");
        }
        if (!execution.Started)
            return Failure(execution.FailureCode ?? "worker_start_failed");
        if (execution.ExitCode != 0)
            return Failure("worker_failed");
        if (!string.IsNullOrWhiteSpace(execution.FailureCode))
            return Failure(execution.FailureCode);
        if (string.IsNullOrWhiteSpace(execution.StandardOutput))
            return Failure("worker_empty_response");

        WindowStructureSnapshot? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<WindowStructureSnapshot>(
                execution.StandardOutput);
        }
        catch (JsonException)
        {
            return Failure("worker_invalid_response");
        }

        if (snapshot is null)
            return Failure("worker_invalid_response");
        if (!snapshot.Success)
            return Failure(NormalizeFailureCode(snapshot.FailureCode));
        if (!IsValidHash(snapshot.TreeHash)
            || snapshot.ElementCount <= 0
            || snapshot.ElementCount > FlaUiWindowStructureSnapshotProvider.MaximumElements(profile)
            || snapshot.WindowHandle != windowHandle.ToInt64()
            || snapshot.ProcessId != boundProcessId)
        {
            return Failure("worker_invalid_snapshot");
        }

        if (_processIdentity is not null
            && (!_processIdentity.TryGetProcessId(windowHandle, out var finalProcessId)
                || finalProcessId != boundProcessId))
        {
            return Failure("window_process_changed");
        }

        return snapshot with { FailureCode = null };
    }

    private static WindowStructureSnapshot Failure(string? code) =>
        new(false, null, 0, false, NormalizeFailureCode(code));

    private static string NormalizeFailureCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "capture_failed";
        var normalized = new string(code
            .Take(64)
            .Select(character => char.IsAsciiLetterOrDigit(character) || character == '_'
                ? char.ToLowerInvariant(character)
                : '_')
            .ToArray());
        return string.IsNullOrWhiteSpace(normalized) ? "capture_failed" : normalized;
    }

    private static bool IsValidHash(string? value)
    {
        if (value is null || value.Length != 64) return false;
        try
        {
            _ = Convert.FromHexString(value);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

internal sealed record WindowSnapshotWorkerExecution(
    bool Started,
    bool TimedOut,
    bool Terminated,
    int ExitCode,
    string? StandardOutput,
    string? FailureCode);

internal interface IWindowSnapshotWorkerProcess
{
    WindowSnapshotWorkerExecution Execute(
        string executablePath,
        nint windowHandle,
        int expectedProcessId,
        WindowStructureCaptureProfile profile,
        TimeSpan timeout);
}

internal sealed class SystemWindowSnapshotWorkerProcess : IWindowSnapshotWorkerProcess
{
    private const int MaximumResponseCharacters = 16 * 1024;
    private readonly object _executionLock = new();
    private int? _unterminatedProcessId;

    public WindowSnapshotWorkerExecution Execute(
        string executablePath,
        nint windowHandle,
        int expectedProcessId,
        WindowStructureCaptureProfile profile,
        TimeSpan timeout)
    {
        lock (_executionLock)
        {
            if (!TryReapUnterminatedWorker())
                return Failed("worker_isolation_compromised");
            return ExecuteSingle(
                executablePath,
                windowHandle,
                expectedProcessId,
                profile,
                timeout);
        }
    }

    private WindowSnapshotWorkerExecution ExecuteSingle(
        string executablePath,
        nint windowHandle,
        int expectedProcessId,
        WindowStructureCaptureProfile profile,
        TimeSpan timeout)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executablePath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };
            if (string.Equals(
                    Path.GetFileNameWithoutExtension(executablePath),
                    "dotnet",
                    StringComparison.OrdinalIgnoreCase))
            {
                var entryAssemblyPath = System.Reflection.Assembly.GetEntryAssembly()?.Location;
                if (string.IsNullOrWhiteSpace(entryAssemblyPath)
                    || !File.Exists(entryAssemblyPath))
                {
                    return Failed("worker_entrypoint_unavailable");
                }
                process.StartInfo.ArgumentList.Add(entryAssemblyPath);
            }
            process.StartInfo.ArgumentList.Add(UiaSnapshotWorkerMode.Switch);
            process.StartInfo.ArgumentList.Add("--window-handle");
            process.StartInfo.ArgumentList.Add(
                windowHandle.ToInt64().ToString(CultureInfo.InvariantCulture));
            process.StartInfo.ArgumentList.Add("--expected-process-id");
            process.StartInfo.ArgumentList.Add(
                expectedProcessId.ToString(CultureInfo.InvariantCulture));
            process.StartInfo.ArgumentList.Add("--capture-profile");
            process.StartInfo.ArgumentList.Add(
                profile == WindowStructureCaptureProfile.Pms ? "pms" : "multi-app");

            if (!process.Start())
                return Failed("worker_start_failed");

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(checked((int)Math.Ceiling(timeout.TotalMilliseconds))))
            {
                var terminated = false;
                try
                {
                    process.Kill(entireProcessTree: true);
                    terminated = process.WaitForExit(1000);
                }
                catch
                {
                    try { terminated = process.HasExited; }
                    catch { terminated = false; }
                }

                if (!terminated)
                    _unterminatedProcessId = process.Id;

                return new WindowSnapshotWorkerExecution(
                    true,
                    true,
                    terminated,
                    -1,
                    null,
                    "provider_timeout");
            }

            var stdout = stdoutTask.GetAwaiter().GetResult();
            _ = stderrTask.GetAwaiter().GetResult();
            if (stdout.Length > MaximumResponseCharacters)
                return Failed("worker_response_too_large", started: true, exitCode: process.ExitCode);

            return new WindowSnapshotWorkerExecution(
                true,
                false,
                true,
                process.ExitCode,
                stdout,
                null);
        }
        catch (Exception)
        {
            return Failed("worker_start_failed");
        }
    }

    private bool TryReapUnterminatedWorker()
    {
        if (_unterminatedProcessId is not { } processId) return true;

        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                if (!process.WaitForExit(1000)) return false;
            }
            _unterminatedProcessId = null;
            return true;
        }
        catch (ArgumentException)
        {
            // PID no longer exists.
            _unterminatedProcessId = null;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static WindowSnapshotWorkerExecution Failed(
        string code,
        bool started = false,
        int exitCode = -1) =>
        new(started, false, true, exitCode, null, code);
}

/// <summary>
/// Worker-side protocol invoked by the parent through the same signed Helper
/// executable. Output is one bounded JSON object containing structural truth.
/// </summary>
internal static class UiaSnapshotWorkerMode
{
    internal const string Switch = "--uia-snapshot-worker";

    internal static bool TryRun(
        IReadOnlyList<string> args,
        TextWriter output,
        out int exitCode)
    {
        exitCode = 0;
        if (args.Count == 0 || !string.Equals(args[0], Switch, StringComparison.Ordinal))
            return false;

        WindowStructureSnapshot snapshot;
        if (!TryReadArgument(args, "--window-handle", out var handleValue)
            || !long.TryParse(
                handleValue,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var rawHandle)
            || rawHandle <= 0)
        {
            snapshot = new WindowStructureSnapshot(
                false,
                null,
                0,
                false,
                "invalid_window");
            exitCode = 2;
        }
        else if (!TryReadArgument(args, "--expected-process-id", out var processIdValue)
                 || !int.TryParse(
                     processIdValue,
                     NumberStyles.None,
                     CultureInfo.InvariantCulture,
                     out var expectedProcessId)
                 || expectedProcessId <= 0)
        {
            snapshot = new WindowStructureSnapshot(
                false,
                null,
                0,
                false,
                "invalid_expected_process");
            exitCode = 2;
        }
        else if (!TryReadArgument(args, "--capture-profile", out var profileValue)
                 || !TryParseProfile(profileValue, out var profile))
        {
            snapshot = new WindowStructureSnapshot(
                false,
                null,
                0,
                false,
                "invalid_capture_profile");
            exitCode = 2;
        }
        else
        {
            using var logger = new LoggerConfiguration()
                .MinimumLevel.Fatal()
                .CreateLogger();
            snapshot = new FlaUiWindowStructureSnapshotProvider(logger)
                .Capture((nint)rawHandle, expectedProcessId, profile);
        }

        output.Write(JsonSerializer.Serialize(snapshot));
        return true;
    }

    private static bool TryReadArgument(
        IReadOnlyList<string> args,
        string name,
        out string value)
    {
        for (var index = 1; index + 1 < args.Count; index++)
        {
            if (!string.Equals(args[index], name, StringComparison.Ordinal)) continue;
            value = args[index + 1];
            return true;
        }
        value = string.Empty;
        return false;
    }

    private static bool TryParseProfile(
        string value,
        out WindowStructureCaptureProfile profile)
    {
        if (string.Equals(value, "pms", StringComparison.Ordinal))
        {
            profile = WindowStructureCaptureProfile.Pms;
            return true;
        }
        if (string.Equals(value, "multi-app", StringComparison.Ordinal))
        {
            profile = WindowStructureCaptureProfile.MultiApp;
            return true;
        }
        profile = default;
        return false;
    }
}

/// <summary>
/// Worker-only bounded UIA walker. Its cooperative budget limits healthy
/// providers; the parent process deadline remains the hard boundary.
/// </summary>
internal sealed class FlaUiWindowStructureSnapshotProvider : IWindowStructureSnapshotProvider
{
    private const int MultiAppMaximumElements = 512;
    private const int PmsMaximumElements = 5000;
    private const int MultiAppMaximumDepth = 6;
    private const int PmsMaximumDepth = 8;
    private static readonly TimeSpan WallClockBudget = TimeSpan.FromSeconds(2);

    private readonly ILogger _logger;
    private readonly IWindowProcessIdentityResolver _processIdentity;

    internal FlaUiWindowStructureSnapshotProvider(
        ILogger logger,
        IWindowProcessIdentityResolver? processIdentity = null)
    {
        _logger = logger.ForContext<FlaUiWindowStructureSnapshotProvider>();
        _processIdentity = processIdentity ?? new Win32WindowProcessIdentityResolver();
    }

    internal static int MaximumElements(WindowStructureCaptureProfile profile) =>
        profile == WindowStructureCaptureProfile.Pms
            ? PmsMaximumElements
            : MultiAppMaximumElements;

    public WindowStructureSnapshot Capture(
        nint windowHandle,
        int? expectedProcessId = null,
        WindowStructureCaptureProfile profile = WindowStructureCaptureProfile.MultiApp)
    {
        if (!OperatingSystem.IsWindows())
            return Failure("unsupported_platform");
        if (windowHandle == 0)
            return Failure("invalid_window");
        var boundProcessId = expectedProcessId.GetValueOrDefault();
        if (boundProcessId <= 0)
            return Failure("invalid_expected_process");
        if (!_processIdentity.TryGetProcessId(windowHandle, out var initialProcessId)
            || initialProcessId != boundProcessId)
        {
            return Failure("window_process_mismatch");
        }

        var maximumElements = MaximumElements(profile);
        var maximumDepth = profile == WindowStructureCaptureProfile.Pms
            ? PmsMaximumDepth
            : MultiAppMaximumDepth;

        UIA2Automation? automation = null;
        try
        {
            automation = new UIA2Automation();
            var root = automation.FromHandle(windowHandle);
            if (root is null) return Failure("window_unavailable");
            int rootProcessId;
            try { rootProcessId = root.Properties.ProcessId.ValueOrDefault; }
            catch { return Failure("provider_failure"); }
            if (rootProcessId != boundProcessId)
                return Failure("uia_process_mismatch");

            var stopwatch = Stopwatch.StartNew();
            var queue = new Queue<(AutomationElement Element, int Depth, int ChildIndex)>();
            var structuralParts = new List<string>(maximumElements);
            var truncated = false;
            queue.Enqueue((root, 0, 0));

            while (queue.Count > 0
                   && structuralParts.Count < maximumElements
                   && stopwatch.Elapsed < WallClockBudget)
            {
                var (element, depth, childIndex) = queue.Dequeue();
                if (!TryRead(
                        () => element.Properties.ControlType.ValueOrDefault.ToString(),
                        out var controlType)
                    || !TryRead(
                        () => element.Properties.AutomationId.ValueOrDefault,
                        out var automationId)
                    || !TryRead(
                        () => element.Properties.ClassName.ValueOrDefault,
                        out var className))
                {
                    return Failure("provider_failure");
                }

                if (!string.IsNullOrWhiteSpace(controlType)
                    || !string.IsNullOrWhiteSpace(automationId)
                    || !string.IsNullOrWhiteSpace(className))
                {
                    structuralParts.Add(
                        $"{depth}|{childIndex}|{controlType}|{automationId}|{className}");
                }

                if (depth >= maximumDepth)
                {
                    try
                    {
                        if (element.FindAllChildren().Length > 0)
                            truncated = true;
                    }
                    catch
                    {
                        return Failure("provider_failure");
                    }
                    continue;
                }
                AutomationElement[] children;
                try { children = element.FindAllChildren(); }
                catch { return Failure("provider_failure"); }

                for (var index = 0; index < children.Length; index++)
                {
                    if (queue.Count + structuralParts.Count >= maximumElements)
                    {
                        truncated = true;
                        break;
                    }
                    queue.Enqueue((children[index], depth + 1, index));
                }
            }

            if (structuralParts.Count == 0)
                return Failure("empty_tree");
            if (!_processIdentity.TryGetProcessId(windowHandle, out var finalProcessId)
                || finalProcessId != boundProcessId)
            {
                return Failure("window_process_changed");
            }

            var treeHash = Convert.ToHexString(SHA256.HashData(
                    Encoding.UTF8.GetBytes(string.Join('\n', structuralParts))))
                .ToLowerInvariant();
            truncated |= queue.Count > 0;
            if (truncated)
            {
                _logger.Debug(
                    "Multi-app UIA snapshot truncated at {ElementCount} structural elements",
                    structuralParts.Count);
            }

            return new WindowStructureSnapshot(
                true,
                treeHash,
                structuralParts.Count,
                truncated,
                null,
                windowHandle.ToInt64(),
                boundProcessId);
        }
        catch (Exception ex)
        {
            _logger.Warning(
                "Multi-app UIA provider failed ({ExceptionType})",
                ex.GetType().FullName);
            return Failure("provider_exception");
        }
        finally
        {
            automation?.Dispose();
        }
    }

    private static WindowStructureSnapshot Failure(string code) =>
        new(false, null, 0, false, code);

    private static bool TryRead(Func<string?> read, out string? value)
    {
        try
        {
            value = read();
            return true;
        }
        catch
        {
            value = null;
            return false;
        }
    }
}
