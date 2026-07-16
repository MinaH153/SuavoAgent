using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace SuavoAgent.Helper.Vision;

internal sealed class NativeOcrTimeoutException : TimeoutException
{
    internal NativeOcrTimeoutException() : base("Native OCR exceeded its execution budget.") { }
}

/// <summary>
/// Native Tesseract cannot be cancelled once engine.Process enters unmanaged
/// code. The Helper is already the isolation process, so the only safe timeout
/// response is fail-stop: Broker/Watchdog restarts a clean Helper rather than
/// allowing a hung thread, engine reuse, or disposal during native execution.
/// </summary>
internal static class NativeOcrWatchdog
{
    internal const int FailStopExitCode = 173;

    internal static async Task<T> RunAsync<T>(
        Func<T> operation,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Action failStop)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(failStop);
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        cancellationToken.ThrowIfCancellationRequested();

        // Native OCR is synchronous and may block forever. Keep it off the
        // shared thread pool so a wedged engine cannot also starve the timeout
        // continuation that must terminate this Helper process.
        var operationTask = Task.Factory.StartNew(
            operation,
            CancellationToken.None,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);
        var timeoutTask = Task.Delay(timeout, CancellationToken.None);
        var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        var completed = await Task.WhenAny(operationTask, timeoutTask, cancellationTask)
            .ConfigureAwait(false);
        if (completed == operationTask)
            return await operationTask.ConfigureAwait(false);

        // Never return a live native operation to the caller. Production's
        // failStop terminates this Helper process; the exception is solely a
        // deterministic test/fallback path if termination unexpectedly returns.
        failStop();
        if (completed == cancellationTask)
            throw new OperationCanceledException(cancellationToken);
        throw new NativeOcrTimeoutException();
    }

    [DoesNotReturn]
    internal static void TerminateCurrentHelper()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            Environment.Exit(FailStopExitCode);
        }

        // Kill normally never returns long enough for this line to execute.
        // Environment.Exit is a no-dump fallback, unlike FailFast/Windows WER.
        Environment.Exit(FailStopExitCode);
    }
}
