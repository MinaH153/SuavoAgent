using Serilog;
using Serilog.Core;
using Serilog.Events;
using SuavoAgent.Helper;
using SuavoAgent.Helper.Actuation;
using Xunit;

namespace SuavoAgent.Helper.Tests;

// The FSD eval's Observe stage was structurally 0 on a bare sim box (Queen, 2026-07-04)
// because the Helper gates its whole UIA attach loop on PioneerRxInstallDetector, which
// only sees a REAL PioneerRx install (path/registry) — a sim satisfies neither, so the
// interaction observer never subscribes. ShouldPollForPms adds an explicit, off-by-default
// eval override so a sim box can attach + observe. Pin that contract here.
public class PioneerRxInstallDetectorForceAttachTests
{
    [Fact]
    public void ForceEnvironmentVariable_CannotEnableAttachWithoutSignedApproval()
    {
        // Only meaningful on non-Windows CI, where IsInstalled() is guaranteed false —
        // so a true result here can ONLY come from the override, not a stray real install.
        if (OperatingSystem.IsWindows()) return;

        WithEnv("1", () =>
        {
            var sink = new CapturingSink();
            var log = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();

            var trust = new PioneerRxProcessTrustVerifier(
                PioneerRxApprovalLoadResult.Denied("pioneerrx_not_approved"));
            Assert.False(PioneerRxInstallDetector.ShouldPollForPms(log, trust));
        });
    }

    [Fact]
    public void ShouldPollForPms_WhenEnvUnset_FallsBackToIsInstalled()
    {
        if (OperatingSystem.IsWindows()) return; // non-Windows: IsInstalled() is false
        var trust = new PioneerRxProcessTrustVerifier(
            PioneerRxApprovalLoadResult.Denied("pioneerrx_not_approved"));
        WithEnv(null, () => Assert.False(PioneerRxInstallDetector.ShouldPollForPms(SilentLogger(), trust)));
    }

    [Fact]
    public void ShouldPollForPms_WhenEnvIsNotExactlyOne_DoesNotForce()
    {
        if (OperatingSystem.IsWindows()) return;
        // Guard against a loose truthy check leaking the override on "true"/"0"/"yes".
        foreach (var v in new[] { "0", "true", "yes", "" })
        {
            var trust = new PioneerRxProcessTrustVerifier(
                PioneerRxApprovalLoadResult.Denied("pioneerrx_not_approved"));
            WithEnv(v, () => Assert.False(PioneerRxInstallDetector.ShouldPollForPms(SilentLogger(), trust)));
        }
    }

    [Fact]
    public void DetectionProbeException_FailsClosed()
    {
        var installed = PioneerRxInstallDetector.IsInstalledFromProbes(
            _ => throw new IOException("synthetic"),
            _ => "ignored",
            SilentLogger());
        Assert.False(installed);
    }

    private static void WithEnv(string? value, Action body)
    {
        var prev = Environment.GetEnvironmentVariable(PioneerRxInstallDetector.ForceAttachEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(PioneerRxInstallDetector.ForceAttachEnvVar, value);
            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(PioneerRxInstallDetector.ForceAttachEnvVar, prev);
        }
    }

    private static ILogger SilentLogger() =>
        new LoggerConfiguration().WriteTo.Sink(new CapturingSink()).CreateLogger();

    private sealed class CapturingSink : ILogEventSink
    {
        public List<string> Messages { get; } = new();
        public void Emit(LogEvent logEvent) => Messages.Add(logEvent.RenderMessage());
    }
}
