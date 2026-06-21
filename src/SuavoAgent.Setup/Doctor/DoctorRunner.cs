// src/SuavoAgent.Setup/Doctor/DoctorRunner.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.Intrinsics.X86;
using System.Threading;
using System.Threading.Tasks;
using SuavoAgent.Setup.Preflight;
using SuavoAgent.Setup.Verify;

namespace SuavoAgent.Setup.Doctor;

/// <summary>Runs the full read-only health layer-trace and prints a table + writes doctor-report.json.</summary>
public static class DoctorRunner
{
    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SuavoAgent");
        var version = ReadCoreVersion() ?? "(unknown)";

        var layers = new List<GateResult>
        {
            new VersionProbe(ReadCoreVersion).Check(),
            VcRedistGate(),
            new CpuVariantProbe(() => Avx2.IsSupported, ReadVariantMarker).Check(),
            ServiceInstaller.ServicesRunningGate(),
            await new PipePingProbe().CheckAsync(ct),
            new BrainHealthProbe().Check(),
            new SqlHealthProbe().Check(),
            new CloudAuthHealthProbe().Check(),
            new ConfigDoctorProbe().Check(),
        };

        var report = new DoctorReport(version, layers);
        try { File.WriteAllText(Path.Combine(dataDir, "doctor-report.json"), DoctorReport.ToJson(report)); }
        catch { /* best-effort */ }
        Console.WriteLine(DoctorReport.ToTable(report));
        return report.HasFailure ? 1 : 0;
    }

    private static GateResult VcRedistGate()
    {
        var s = new VcRedistChecker().Check();
        return s.Installed
            ? new GateResult("VC++ runtime", GateState.Ok, $"present{(s.Version is null ? "" : $" ({s.Version})")}")
            : new GateResult("VC++ runtime", GateState.Fail,
                $"missing [{string.Join(", ", s.MissingDlls)}] — the brain cannot load. Install VC++ 2015-2022 x64 Redistributable.");
    }

    private static string? ReadCoreVersion()
    {
        // Best-effort: probe the default install dir; FileVersionInfo does NOT load the assembly.
        foreach (var dir in new[] { @"C:\Program Files\Suavo\Agent", @"C:\Program Files\SuavoAgent" })
        {
            var p = Path.Combine(dir, "SuavoAgent.Core.exe");
            try { if (File.Exists(p)) return FileVersionInfo.GetVersionInfo(p).ProductVersion; }
            catch { /* try next */ }
        }
        return null;
    }

    private static string? ReadVariantMarker()
    {
        var p = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SuavoAgent", "native", ".variant");
        try { return File.Exists(p) ? File.ReadAllText(p) : null; }
        catch { return null; }
    }
}
