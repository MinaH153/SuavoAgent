using System;
using System.IO;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Broker.Honeytoken;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Contracts.Models;
using Xunit;

namespace SuavoAgent.Broker.Tests.Honeytoken;

/// <summary>
/// LIVE round-trip of the real ETW oracle — the ONLY check of the actual session wiring (provider + keyword
/// correctness, the Create callback, the decoy filter firing, the handoff written). Headless tests can't catch
/// a mis-keyworded session (e.g. 0x1000 instead of 0x90) — it would pass every static test yet emit no
/// FileName events. Runs ONLY in the windows-uia-smoke CI job (windows-latest, admin, continue-on-error):
/// no-op pass off-Windows; graceful no-op if the runner can't start a kernel session; FAIL only if the session
/// armed but a real decoy open produced no attribution (the wiring bug we want to catch before a real box).
/// </summary>
public sealed class EtwHoneytokenLiveSmokeTests
{
    [Fact]
    public void LiveOracle_RealDecoyOpen_ProducesAttribution_NoPhiPathLeak()
    {
        if (!OperatingSystem.IsWindows()) return; // no-op on the ubuntu/macOS gate

        var root = Path.Combine(Path.GetTempPath(), $"suavo-hny-smoke-{Guid.NewGuid():N}");
        var decoyDir = Path.Combine(root, "SuavoAgent", HoneytokenConstants.DirName);
        var handoffPath = Path.Combine(root, "SuavoAgent", HoneytokenAttributionStore.FileName);
        Directory.CreateDirectory(decoyDir);
        var decoyPath = Path.Combine(decoyDir, HoneytokenConstants.TokenFileName);
        File.WriteAllBytes(decoyPath, HoneytokenConstants.TokenContent);
        const string nonce = "smoke-nonce";

        var tracer = new EtwHoneytokenFileTracer(
            NullLogger<EtwHoneytokenFileTracer>.Instance, decoyDir, handoffPath, nonce);

        try
        {
            tracer.ArmForTest();
        }
        catch (Exception)
        {
            // Non-elevated / kernel-session-unavailable runner — informational job, no-op rather than fail.
            tracer.Dispose();
            TryDelete(root);
            return;
        }

        try
        {
            // Generate a real IRP_MJ_CREATE (handle-open) on the decoy.
            for (var i = 0; i < 3 && !File.Exists(handoffPath); i++)
            {
                using (var fs = File.Open(decoyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    _ = fs.ReadByte();
                }
                // Poll up to ~5s for the ETW callback → handoff write.
                for (var w = 0; w < 50 && !File.Exists(handoffPath); w++) Thread.Sleep(100);
            }

            if (!File.Exists(handoffPath))
            {
                // Armed but no handoff after a real open = a wiring/keyword regression. FAIL loudly.
                Assert.True(false, "ETW oracle armed but a real decoy open produced no handoff (keyword/wiring regression)");
            }

            var entries = HoneytokenAttributionStore.Read(handoffPath, nonce, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
            Assert.NotEmpty(entries);
            Assert.All(entries, e => Assert.Equal(HoneytokenConstants.ComputeId(), e.TokenId)); // PHI-clamp held
            // The toucher is THIS test host (testhost/dotnet) — proves perceive→resolve→handoff end-to-end.
            Assert.Contains(entries, e => !string.IsNullOrEmpty(e.ProcessName));

            // PHI proof: the handoff file must contain ONLY the decoy id, never a raw path.
            var raw = File.ReadAllText(handoffPath);
            Assert.DoesNotContain(@"\Device\", raw);
            Assert.DoesNotContain("backup_credentials", raw);
        }
        finally
        {
            tracer.Dispose();
            TryDelete(root);
        }
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
    }
}
