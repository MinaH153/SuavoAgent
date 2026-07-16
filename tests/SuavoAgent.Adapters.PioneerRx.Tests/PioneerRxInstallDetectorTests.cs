using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Adapters.PioneerRx;
using Xunit;

namespace SuavoAgent.Adapters.PioneerRx.Tests;

public class PioneerRxInstallDetectorTests
{
    [Fact]
    public void IsInstalled_NonWindows_ReturnsFalse()
    {
        if (OperatingSystem.IsWindows())
        {
            // On Windows we'd need a fixture host to assert false reliably —
            // skip here so the cross-platform invariant test still runs on
            // every CI architecture without requiring a clean-room Windows VM.
            return;
        }

        var captured = new CapturingLogger();
        var result = PioneerRxInstallDetector.IsInstalled(captured);

        Assert.False(result);
        Assert.Empty(captured.Entries);
    }

    [Fact]
    public void IsInstalled_NonWindows_DoesNotThrow()
    {
        if (OperatingSystem.IsWindows()) return;

        var ex = Record.Exception(() =>
            PioneerRxInstallDetector.IsInstalled(NullLogger.Instance));

        Assert.Null(ex);
    }

    [Fact]
    public void ProbeFailure_IsIndeterminateAndCannotActivateCapability()
    {
        var result = PioneerRxInstallDetector.DetectFromProbes(
            _ => throw new IOException("synthetic"),
            _ => null,
            NullLogger.Instance);

        Assert.Equal(PioneerRxInstallDetector.DetectionStatus.Indeterminate, result.Status);
        Assert.Equal("probe_failed", result.Code);
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Entries { get; } = new();

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(formatter(state, exception));
        }
    }
}
