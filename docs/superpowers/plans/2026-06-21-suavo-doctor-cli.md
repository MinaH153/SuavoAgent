# Suavo Doctor (on-box diagnostic CLI) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** One command — `SuavoSetup.exe --doctor` — runs a full agent-health layer-trace and prints a LAYER/STATUS/DETAIL table + writes `doctor-report.json`, productizing the manual on-box diagnostic that found every Nadim root cause (missing VC++, brain native-load failure, SQL auth, wrong pricing modality). Read-only, no PHI, no PMS contact.

**Architecture:** A new `--doctor` mode on the existing `SuavoSetup.exe` (routed in `Program.cs` alongside `--console`/`--uninstall`). A `DoctorRunner` composes layers as `GateResult`s (reused from the Verify namespace): the four install-verify probes already built (Services, Pipe, Brain, Cloud auth) + `VcRedistChecker` + three new read-only probes (`SqlHealthProbe` reads the Core log for SQL outcomes; `VersionProbe` reads Core.exe's file version; `ConfigDoctorProbe` reads the plain `config-overrides.json` for effective pricing modality + a security flag) + a `CpuVariantProbe` (AVX2 vs the installed `.variant`). The report is a `DoctorReport` (pure, testable) rendered by a pure formatter and serialized to JSON; `DoctorRunner` is a thin composition over these.

**Tech Stack:** .NET 8, C#, xUnit 2.9.2 (`tests/SuavoAgent.Setup.Tests/`), reuses `SuavoAgent.Setup.Verify.{GateResult,GateState}`, `System.Diagnostics.FileVersionInfo`, `System.Runtime.Intrinsics.X86.Avx2`, `System.Text.Json`.

## Global Constraints

- **Read-only + HIPAA-safe:** every probe only reads service state, log text, plain health/override JSON, file versions, and CPU flags. Never writes config, never reads PHI/PMS, never reads DPAPI-sealed secrets (ApiKey/SqlPassword live in sealed appsettings — out of scope and not readable).
- **Reuse `GateState { Ok, Fail, Warn, Skip }` + `GateResult(Name, State, Detail)`** from `SuavoAgent.Setup.Verify` as the layer-result type — do NOT invent a new layer model.
- **`Fail` = a definitive problem; `Warn` = inconclusive/absent; `Skip` = N/A.** The doctor's exit code is `1` if ANY layer is `Fail`, else `0` (advisory; the report always prints regardless).
- **Newest-`core-*.log` classification** mirrors the existing `BrainHealthProbe` exactly (read `%PROGRAMDATA%\SuavoAgent\logs\core-*.log`, newest by LastWriteTimeUtc, fail-soft to `null`).
- **Exact paths** (use `Environment.SpecialFolder.CommonApplicationData` + `"SuavoAgent"`): config overrides `config-overrides.json`; native variant `native\.variant`; report output `doctor-report.json`.
- **Defaults when a config value is absent from config-overrides.json:** `Agent.PricingExecutor` → `"UiaFirst"`; `Agent.SqlTrustServerCertificate` → `true`; `Agent.RelaxIpcClientPathValidation` → `false`.
- New probe code under `src/SuavoAgent.Setup/Doctor/`; tests under `tests/SuavoAgent.Setup.Tests/Doctor/`. File-scoped namespaces.

---

### Task 1: `SqlHealthProbe` — classify the Core log for SQL outcome

**Files:**
- Create: `src/SuavoAgent.Setup/Doctor/SqlHealthProbe.cs`
- Test: `tests/SuavoAgent.Setup.Tests/Doctor/SqlHealthProbeTests.cs`

**Interfaces:**
- Consumes: `GateResult`/`GateState` from `SuavoAgent.Setup.Verify`.
- Produces: `SqlHealthProbe(Func<string?> readCoreLog)` with `GateResult Check()`. Classification priority (first match wins, case-insensitive `Contains`): `"ANONYMOUS LOGON"` or `"Login failed"` or `"18456"` → `Fail` ("SQL auth failing — service account has no SQL login; use SQL Auth or grant access"); `"certificate chain"` or `"not trusted"` → `Fail` ("SQL TLS cert not trusted — set Agent.SqlTrustServerCertificate=true"); `"SQL schema fingerprint failed"` → `Fail`; `"SQL connection failed"` → `Fail` ("SQL unreachable"); `"SQL connected to"` → `Ok`; none → `Warn` ("no SQL activity logged — pricing may be UiaFirst").

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SuavoAgent.Setup.Tests/Doctor/SqlHealthProbeTests.cs
using SuavoAgent.Setup.Doctor;
using SuavoAgent.Setup.Verify;
using Xunit;

namespace SuavoAgent.Setup.Tests.Doctor;

public class SqlHealthProbeTests
{
    private static GateResult Run(string? log) => new SqlHealthProbe(() => log).Check();

    [Fact]
    public void Anonymous_logon_is_Fail()
    {
        var r = Run("WRN SQL connection failed\nLogin failed for user 'NT AUTHORITY\\ANONYMOUS LOGON'. Error Number:18456");
        Assert.Equal(GateState.Fail, r.State);
        Assert.Contains("auth", r.Detail, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cert_not_trusted_is_Fail_with_trust_remediation()
    {
        var r = Run("WRN SQL connection failed\n(SSL Provider) The certificate chain was issued by an authority that is not trusted");
        Assert.Equal(GateState.Fail, r.State);
        Assert.Contains("SqlTrustServerCertificate", r.Detail);
    }

    [Fact]
    public void Connected_is_Ok()
        => Assert.Equal(GateState.Ok, Run("INF SQL connected to PIONEERSERVER\\PHARMACY").State);

    [Fact]
    public void No_sql_activity_is_Warn()
        => Assert.Equal(GateState.Warn, Run("INF Tier-2 LocalInference ENABLED").State);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SuavoAgent.Setup.Tests/SuavoAgent.Setup.Tests.csproj --filter "FullyQualifiedName~SqlHealthProbeTests"`
Expected: FAIL (type missing).

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/SuavoAgent.Setup/Doctor/SqlHealthProbe.cs
using System;
using System.IO;
using System.Linq;
using SuavoAgent.Setup.Verify;

namespace SuavoAgent.Setup.Doctor;

/// <summary>Classifies the newest core-*.log for the SQL connection outcome. Read-only, fail-soft.</summary>
public sealed class SqlHealthProbe
{
    private readonly Func<string?> _readCoreLog;

    public SqlHealthProbe(Func<string?>? readCoreLog = null)
        => _readCoreLog = readCoreLog ?? ReadNewestCoreLog;

    public GateResult Check()
    {
        var log = _readCoreLog();
        if (string.IsNullOrEmpty(log))
            return new GateResult("SQL", GateState.Warn, "No Core log yet");

        bool Has(string s) => log.Contains(s, StringComparison.OrdinalIgnoreCase);

        if (Has("ANONYMOUS LOGON") || Has("Login failed") || Has("18456"))
            return new GateResult("SQL", GateState.Fail,
                "SQL auth failing — the service account has no SQL login. Use SQL Auth or grant the account DB access.");
        if (Has("certificate chain") || Has("not trusted"))
            return new GateResult("SQL", GateState.Fail,
                "SQL TLS cert not trusted — set Agent.SqlTrustServerCertificate=true (PioneerRx uses a self-signed cert).");
        if (Has("SQL schema fingerprint failed"))
            return new GateResult("SQL", GateState.Fail, "Connected DB is not PioneerRx (schema fingerprint failed).");
        if (Has("SQL connection failed"))
            return new GateResult("SQL", GateState.Fail, "SQL server unreachable.");
        if (Has("SQL connected to"))
            return new GateResult("SQL", GateState.Ok, "SQL connected.");
        return new GateResult("SQL", GateState.Warn, "No SQL activity logged (pricing may be UiaFirst — no SQL needed).");
    }

    private static string? ReadNewestCoreLog()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "SuavoAgent", "logs");
            if (!Directory.Exists(dir)) return null;
            var newest = new DirectoryInfo(dir).GetFiles("core-*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (newest is null) return null;
            using var fs = new FileStream(newest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            return reader.ReadToEnd();
        }
        catch { return null; }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/SuavoAgent.Setup.Tests/SuavoAgent.Setup.Tests.csproj --filter "FullyQualifiedName~SqlHealthProbeTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/SuavoAgent.Setup/Doctor/SqlHealthProbe.cs tests/SuavoAgent.Setup.Tests/Doctor/SqlHealthProbeTests.cs
git commit -m "feat(doctor): SqlHealthProbe — classify Core log for SQL connection outcome"
```

---

### Task 2: `VersionProbe` + `CpuVariantProbe` — environment layers

**Files:**
- Create: `src/SuavoAgent.Setup/Doctor/VersionProbe.cs`
- Create: `src/SuavoAgent.Setup/Doctor/CpuVariantProbe.cs`
- Test: `tests/SuavoAgent.Setup.Tests/Doctor/EnvironmentProbesTests.cs`

**Interfaces:**
- Consumes: `GateResult`/`GateState`.
- Produces:
  - `VersionProbe(Func<string?> readCoreFileVersion)` with `GateResult Check()` → `Ok` with the version in Detail if readable; `Warn` ("agent version unknown") if null. (Real reader uses `FileVersionInfo.GetVersionInfo(Path.Combine(installDir,"SuavoAgent.Core.exe")).ProductVersion`, lightweight — does NOT load the assembly.)
  - `CpuVariantProbe(Func<bool> avx2Supported, Func<string?> readVariantMarker)` with `GateResult Check()` → if CPU supports AVX2 but installed variant is `noavx` → `Warn` ("brain running the slower noavx build though the CPU supports AVX2"); else `Ok` with the variant in Detail. Missing marker is treated as `"noavx"`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SuavoAgent.Setup.Tests/Doctor/EnvironmentProbesTests.cs
using SuavoAgent.Setup.Doctor;
using SuavoAgent.Setup.Verify;
using Xunit;

namespace SuavoAgent.Setup.Tests.Doctor;

public class EnvironmentProbesTests
{
    [Fact]
    public void Version_known_is_Ok_with_version_in_detail()
    {
        var r = new VersionProbe(() => "3.71.0").Check();
        Assert.Equal(GateState.Ok, r.State);
        Assert.Contains("3.71.0", r.Detail);
    }

    [Fact]
    public void Version_unknown_is_Warn()
        => Assert.Equal(GateState.Warn, new VersionProbe(() => null).Check().State);

    [Fact]
    public void Avx2_cpu_with_noavx_build_is_Warn()
        => Assert.Equal(GateState.Warn, new CpuVariantProbe(() => true, () => "noavx").Check().State);

    [Fact]
    public void Matched_variant_is_Ok()
        => Assert.Equal(GateState.Ok, new CpuVariantProbe(() => true, () => "avx2").Check().State);

    [Fact]
    public void Non_avx2_cpu_on_noavx_is_Ok()
        => Assert.Equal(GateState.Ok, new CpuVariantProbe(() => false, () => "noavx").Check().State);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SuavoAgent.Setup.Tests/SuavoAgent.Setup.Tests.csproj --filter "FullyQualifiedName~EnvironmentProbesTests"`
Expected: FAIL.

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/SuavoAgent.Setup/Doctor/VersionProbe.cs
using System;
using SuavoAgent.Setup.Verify;

namespace SuavoAgent.Setup.Doctor;

/// <summary>Reports the installed agent version (Core.exe file version). Read-only.</summary>
public sealed class VersionProbe
{
    private readonly Func<string?> _readCoreFileVersion;
    public VersionProbe(Func<string?> readCoreFileVersion) => _readCoreFileVersion = readCoreFileVersion;

    public GateResult Check()
    {
        var v = _readCoreFileVersion();
        return string.IsNullOrWhiteSpace(v)
            ? new GateResult("Version", GateState.Warn, "Agent version unknown (Core.exe not found)")
            : new GateResult("Version", GateState.Ok, $"SuavoAgent {v}");
    }
}
```

```csharp
// src/SuavoAgent.Setup/Doctor/CpuVariantProbe.cs
using System;
using SuavoAgent.Setup.Verify;

namespace SuavoAgent.Setup.Doctor;

/// <summary>Compares CPU AVX2 capability to the installed brain native-lib variant (.variant marker).</summary>
public sealed class CpuVariantProbe
{
    private readonly Func<bool> _avx2Supported;
    private readonly Func<string?> _readVariantMarker;

    public CpuVariantProbe(Func<bool> avx2Supported, Func<string?> readVariantMarker)
    {
        _avx2Supported = avx2Supported;
        _readVariantMarker = readVariantMarker;
    }

    public GateResult Check()
    {
        var avx2 = _avx2Supported();
        var variant = (_readVariantMarker() ?? "noavx").Trim().ToLowerInvariant();
        if (avx2 && variant == "noavx")
            return new GateResult("Brain CPU variant", GateState.Warn,
                "CPU supports AVX2 but the slower noavx brain build is installed.");
        return new GateResult("Brain CPU variant", GateState.Ok, $"Brain native libs: {variant}");
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/SuavoAgent.Setup.Tests/SuavoAgent.Setup.Tests.csproj --filter "FullyQualifiedName~EnvironmentProbesTests"`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add src/SuavoAgent.Setup/Doctor/VersionProbe.cs src/SuavoAgent.Setup/Doctor/CpuVariantProbe.cs tests/SuavoAgent.Setup.Tests/Doctor/EnvironmentProbesTests.cs
git commit -m "feat(doctor): VersionProbe + CpuVariantProbe (env layers)"
```

---

### Task 3: `ConfigDoctorProbe` — effective config + security flag

**Files:**
- Create: `src/SuavoAgent.Setup/Doctor/ConfigDoctorProbe.cs`
- Test: `tests/SuavoAgent.Setup.Tests/Doctor/ConfigDoctorProbeTests.cs`

**Interfaces:**
- Consumes: `GateResult`/`GateState`.
- Produces: `ConfigDoctorProbe(Func<string?> readConfigOverridesJson)` with `GateResult Check()`. Parses the plain `config-overrides.json`. Reports effective `Agent.PricingExecutor` (default `"UiaFirst"`) + `Agent.SqlTrustServerCertificate` (default `true`) in Detail. If `Agent.RelaxIpcClientPathValidation` is `true` → `Fail` ("RelaxIpcClientPathValidation is ON — disables the PHI pipe security gate"); else `Ok`. Unreadable/missing file → `Ok` with "(defaults)". The override keys may be nested (`{"Agent":{"PricingExecutor":...}}`) or flat (`{"Agent.PricingExecutor":...}`) — handle both.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SuavoAgent.Setup.Tests/Doctor/ConfigDoctorProbeTests.cs
using SuavoAgent.Setup.Doctor;
using SuavoAgent.Setup.Verify;
using Xunit;

namespace SuavoAgent.Setup.Tests.Doctor;

public class ConfigDoctorProbeTests
{
    private static GateResult Run(string? json) => new ConfigDoctorProbe(() => json).Check();

    [Fact]
    public void Reports_effective_pricing_executor_nested()
    {
        var r = Run("{\"Agent\":{\"PricingExecutor\":\"SqlFirst\"}}");
        Assert.Equal(GateState.Ok, r.State);
        Assert.Contains("SqlFirst", r.Detail);
    }

    [Fact]
    public void Reports_effective_pricing_executor_flat()
    {
        var r = Run("{\"Agent.PricingExecutor\":\"SqlFirst\"}");
        Assert.Contains("SqlFirst", r.Detail);
    }

    [Fact]
    public void Missing_file_defaults_to_UiaFirst()
    {
        var r = Run(null);
        Assert.Equal(GateState.Ok, r.State);
        Assert.Contains("UiaFirst", r.Detail);
    }

    [Fact]
    public void Relax_ipc_gate_on_is_Fail()
    {
        var r = Run("{\"Agent\":{\"RelaxIpcClientPathValidation\":true}}");
        Assert.Equal(GateState.Fail, r.State);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SuavoAgent.Setup.Tests/SuavoAgent.Setup.Tests.csproj --filter "FullyQualifiedName~ConfigDoctorProbeTests"`
Expected: FAIL.

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/SuavoAgent.Setup/Doctor/ConfigDoctorProbe.cs
using System;
using System.IO;
using System.Text.Json;
using SuavoAgent.Setup.Verify;

namespace SuavoAgent.Setup.Doctor;

/// <summary>Reads the plain config-overrides.json: reports effective pricing modality + flags a disabled
/// PHI pipe security gate. Never reads DPAPI-sealed secrets.</summary>
public sealed class ConfigDoctorProbe
{
    private readonly Func<string?> _readConfigOverridesJson;

    public ConfigDoctorProbe(Func<string?>? readConfigOverridesJson = null)
        => _readConfigOverridesJson = readConfigOverridesJson ?? ReadDefault;

    public GateResult Check()
    {
        var json = _readConfigOverridesJson();
        var pricing = "UiaFirst";
        var sqlTrust = true;
        var relax = false;
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                pricing = ReadString(doc.RootElement, "PricingExecutor") ?? pricing;
                sqlTrust = ReadBool(doc.RootElement, "SqlTrustServerCertificate") ?? sqlTrust;
                relax = ReadBool(doc.RootElement, "RelaxIpcClientPathValidation") ?? relax;
            }
            catch { /* unreadable → report defaults */ }
        }
        var detail = $"PricingExecutor={pricing}, SqlTrustServerCertificate={sqlTrust}";
        return relax
            ? new GateResult("Config", GateState.Fail,
                $"RelaxIpcClientPathValidation is ON — the PHI pipe security gate is disabled. ({detail})")
            : new GateResult("Config", GateState.Ok, detail);
    }

    // Accepts both flat ("Agent.X") and nested ({"Agent":{"X":...}}) shapes.
    private static string? ReadString(JsonElement root, string key)
    {
        if (root.TryGetProperty($"Agent.{key}", out var flat) && flat.ValueKind == JsonValueKind.String)
            return flat.GetString();
        if (root.TryGetProperty("Agent", out var agent) && agent.ValueKind == JsonValueKind.Object
            && agent.TryGetProperty(key, out var nested) && nested.ValueKind == JsonValueKind.String)
            return nested.GetString();
        return null;
    }

    private static bool? ReadBool(JsonElement root, string key)
    {
        if (root.TryGetProperty($"Agent.{key}", out var flat) && (flat.ValueKind == JsonValueKind.True || flat.ValueKind == JsonValueKind.False))
            return flat.GetBoolean();
        if (root.TryGetProperty("Agent", out var agent) && agent.ValueKind == JsonValueKind.Object
            && agent.TryGetProperty(key, out var nested) && (nested.ValueKind == JsonValueKind.True || nested.ValueKind == JsonValueKind.False))
            return nested.GetBoolean();
        return null;
    }

    private static string? ReadDefault()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SuavoAgent", "config-overrides.json");
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch { return null; }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/SuavoAgent.Setup.Tests/SuavoAgent.Setup.Tests.csproj --filter "FullyQualifiedName~ConfigDoctorProbeTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/SuavoAgent.Setup/Doctor/ConfigDoctorProbe.cs tests/SuavoAgent.Setup.Tests/Doctor/ConfigDoctorProbeTests.cs
git commit -m "feat(doctor): ConfigDoctorProbe — effective pricing modality + PHI-gate flag"
```

---

### Task 4: `DoctorReport` + formatter + `DoctorRunner`

**Files:**
- Create: `src/SuavoAgent.Setup/Doctor/DoctorReport.cs` (record + `ToJson` + pure `ToTable`)
- Create: `src/SuavoAgent.Setup/Doctor/DoctorRunner.cs` (composition + `RunAsync`)
- Test: `tests/SuavoAgent.Setup.Tests/Doctor/DoctorReportTests.cs`

**Interfaces:**
- Consumes: `GateResult`/`GateState`, all probes (T1–T3), reused `ServiceInstaller.ServicesRunningGate`, `PipePingProbe`, `BrainHealthProbe`, `CloudAuthHealthProbe`, `VcRedistChecker`.
- Produces: `record DoctorReport(string Version, IReadOnlyList<GateResult> Layers)` with `bool HasFailure => Layers.Any(l => l.State == GateState.Fail)`, `static string ToJson(DoctorReport)`, and `static string ToTable(DoctorReport)` (pure ASCII table). `DoctorRunner.RunAsync(string[] args, CancellationToken ct) : Task<int>` composes every layer, prints the table, writes `doctor-report.json` to ProgramData (best-effort), and returns `report.HasFailure ? 1 : 0`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SuavoAgent.Setup.Tests/Doctor/DoctorReportTests.cs
using SuavoAgent.Setup.Doctor;
using SuavoAgent.Setup.Verify;
using Xunit;

namespace SuavoAgent.Setup.Tests.Doctor;

public class DoctorReportTests
{
    private static DoctorReport Make(params GateResult[] layers) => new("3.71.0", layers);

    [Fact]
    public void HasFailure_true_when_any_layer_fails()
        => Assert.True(Make(new("A", GateState.Ok, "x"), new("B", GateState.Fail, "y")).HasFailure);

    [Fact]
    public void HasFailure_false_when_only_warn_or_ok()
        => Assert.False(Make(new("A", GateState.Ok, "x"), new("B", GateState.Warn, "y")).HasFailure);

    [Fact]
    public void ToJson_includes_version_and_each_layer()
    {
        var json = DoctorReport.ToJson(Make(new("Brain", GateState.Fail, "native load failed")));
        Assert.Contains("3.71.0", json);
        Assert.Contains("Brain", json);
        Assert.Contains("Fail", json);
    }

    [Fact]
    public void ToTable_renders_each_layer_name_and_state()
    {
        var table = DoctorReport.ToTable(Make(new("SQL", GateState.Fail, "auth failing")));
        Assert.Contains("SQL", table);
        Assert.Contains("Fail", table);
        Assert.Contains("auth failing", table);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SuavoAgent.Setup.Tests/SuavoAgent.Setup.Tests.csproj --filter "FullyQualifiedName~DoctorReportTests"`
Expected: FAIL.

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/SuavoAgent.Setup/Doctor/DoctorReport.cs
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using SuavoAgent.Setup.Verify;

namespace SuavoAgent.Setup.Doctor;

public sealed record DoctorReport(string Version, IReadOnlyList<GateResult> Layers)
{
    public bool HasFailure => Layers.Any(l => l.State == GateState.Fail);

    public static string ToJson(DoctorReport report) => JsonSerializer.Serialize(new
    {
        version = report.Version,
        healthy = !report.HasFailure,
        layers = report.Layers.Select(l => new { name = l.Name, state = l.State.ToString(), detail = l.Detail }),
    }, new JsonSerializerOptions { WriteIndented = true });

    public static string ToTable(DoctorReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"SuavoAgent Doctor — {report.Version}  ({(report.HasFailure ? "DEGRADED" : "healthy")})");
        sb.AppendLine("  LAYER             | STATUS | DETAIL");
        sb.AppendLine("  ------------------+--------+--------------------------------------------------");
        foreach (var l in report.Layers)
            sb.AppendLine($"  {l.Name,-17} | {l.State,-6} | {l.Detail}");
        return sb.ToString();
    }
}
```

```csharp
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
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/SuavoAgent.Setup.Tests/SuavoAgent.Setup.Tests.csproj --filter "FullyQualifiedName~DoctorReportTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/SuavoAgent.Setup/Doctor/DoctorReport.cs src/SuavoAgent.Setup/Doctor/DoctorRunner.cs tests/SuavoAgent.Setup.Tests/Doctor/DoctorReportTests.cs
git commit -m "feat(doctor): DoctorReport (json+table) + DoctorRunner composition"
```

---

### Task 5: Wire `--doctor` into `Program.cs`

**Files:**
- Modify: `src/SuavoAgent.Setup/Program.cs` (add `IsDoctorMode` predicate + dispatch to `DoctorRunner.RunAsync` BEFORE the console-mode check)
- Test: `tests/SuavoAgent.Setup.Tests/Doctor/DoctorModeRoutingTests.cs`

**Interfaces:**
- Consumes: `DoctorRunner.RunAsync` (T4).
- Produces: `Program.IsDoctorMode(string[])` (made internal/visible for test) returns true iff args contain `--doctor` (case-insensitive). When true, `Main` runs `DoctorRunner.RunAsync(args, CancellationToken.None).GetAwaiter().GetResult()` and returns its exit code, before the console/uninstall/GUI branches.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SuavoAgent.Setup.Tests/Doctor/DoctorModeRoutingTests.cs
using Xunit;

namespace SuavoAgent.Setup.Tests.Doctor;

public class DoctorModeRoutingTests
{
    [Fact]
    public void Detects_doctor_flag_case_insensitive()
    {
        Assert.True(SuavoAgent.Setup.Program.IsDoctorMode(new[] { "--Doctor" }));
        Assert.False(SuavoAgent.Setup.Program.IsDoctorMode(new[] { "--console" }));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SuavoAgent.Setup.Tests/SuavoAgent.Setup.Tests.csproj --filter "FullyQualifiedName~DoctorModeRoutingTests"`
Expected: FAIL (`IsDoctorMode` not accessible/defined).

- [ ] **Step 3: Write minimal implementation**

Read `Program.cs` first (the class is likely `internal static class Program`). Then:
1. Add the predicate beside `IsUninstallMode`:
```csharp
internal static bool IsDoctorMode(string[] args) =>
    args.Any(a => string.Equals(a, "--doctor", System.StringComparison.OrdinalIgnoreCase));
```
   (Make `IsDoctorMode` at least `internal` so the test in the same assembly's test project can reach it; if the test project lacks `InternalsVisibleTo`, make it `public`. Match how the test references it — `SuavoAgent.Setup.Program.IsDoctorMode`.)
2. In `Main`, BEFORE the uninstall/console checks, add:
```csharp
if (IsDoctorMode(args))
{
    AttachParentConsole();
    return DoctorRunner.RunAsync(args, System.Threading.CancellationToken.None).GetAwaiter().GetResult();
}
```
   (Use the same console-attach the console path uses, if present; if `AttachParentConsole` doesn't exist, mirror whatever `--console` does to get console output.)

- [ ] **Step 4: Run tests + full Setup suite**

Run: `dotnet test tests/SuavoAgent.Setup.Tests/SuavoAgent.Setup.Tests.csproj`
Expected: PASS (all Setup tests, including every new Doctor test).

- [ ] **Step 5: Commit**

```bash
git add src/SuavoAgent.Setup/Program.cs tests/SuavoAgent.Setup.Tests/Doctor/DoctorModeRoutingTests.cs
git commit -m "feat(doctor): route --doctor to DoctorRunner in Program.cs"
```

---

## Self-Review

**Spec coverage (vs the suavo-doctor spec, on-box CLI slice):**
- ✅ One command (`SuavoSetup.exe --doctor`) traces every layer → table + `doctor-report.json` (T4, T5).
- ✅ Layers: version (T2), VC++ deps + CPU/AVX variant (T2/T4), services + pipe + brain + cloud-auth (reused Phase A/B probes), SQL outcome (T1), effective pricing modality + PHI-gate flag (T3).
- ✅ Read-only, no PHI, no sealed-secret reads (Global Constraints).
- ✅ `Fail` → exit 1 (advisory); report always prints.
- ⏳ **Deferred (noted, not dropped):** the dashboard "Run diagnostics" remote-trigger panel + `doctor --json`/`--layer X` flags + an in-process Core brain-inference trigger are follow-ons (this slice is the on-box CLI). config-sync-health.json surfacing folded into a future enhancement (cloud-auth is covered; config-sync is lower value).

**Placeholder scan:** none. T5 has two "read Program.cs then apply this exact code" notes for the routing integration — unavoidable (depends on the existing arg-parser shape) and the exact insertion code is given.

**Type consistency:** `GateState`/`GateResult` (reused from Verify) used by every probe + `DoctorReport`. `DoctorReport(Version, Layers)` + `HasFailure`/`ToJson`/`ToTable` (T4) consistent. `VcRedistChecker.Check()→VcRedistStatus(Installed,MissingDlls,Version)` wrapped into a `GateResult` in `DoctorRunner.VcRedistGate` (T4). `CpuVariantProbe(Func<bool>,Func<string?>)` and `VersionProbe(Func<string?>)` ctor shapes match their tests.

**Note for the implementer:** Read `Program.cs` in full before T5 and match its actual arg-parsing + console-attach pattern. Every probe is constructor-injectable with delegates so the tests never touch real OS state; `DoctorRunner.RunAsync` is the only piece that hits the real box and is intentionally a thin composition (covered by running `--doctor`, not a unit test).
