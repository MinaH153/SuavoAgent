# Installer Self-Verify (Phase B) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The installer never reports "complete" unless the just-installed agent provably works — services running, Core↔Helper pipe reachable, the on-device brain not in a known-broken state, and cloud auth healthy — closing the "green checkmark over a broken install" gap that hid Nadim's bricked brain.

**Architecture:** Four small, independently-testable read-only probes (`ServicesRunningProbe`, `PipePingProbe`, `BrainHealthProbe`, `CloudAuthHealthProbe`), each returning a `(GateState, detail)` tuple, composed by a `PostInstallVerifier` that produces a `VerifyOutcome` (per-gate results + overall pass/fail + remediation) and writes `install-verify.json`. The verifier is injected with four gate delegates so it is fully unit-testable; thin real adapters supply the production probes. Wired into the GUI `InstallOrchestrator` as a new `Verify` phase (after `InstallServices`, before `Done`) and into the headless `ConsoleInstaller` before its completion summary. A `Fail` gate blocks Success/completion with the exact failing layer + remediation.

**Tech Stack:** .NET 8, C#, xUnit 2.9.2 (`tests/SuavoAgent.Setup.Tests/`), Avalonia 11.2.0 MVVM, `NamedPipeClientStream`, `System.Text.Json`, Serilog log files under `%PROGRAMDATA%\SuavoAgent\logs\`.

## Global Constraints

- **HIPAA/stealth:** verification reads ONLY service state, agent log lines, health JSON, and a pipe ping — never patient data and never the PMS. Do not parse or emit anything but agent-health booleans/markers.
- **A gate blocks Success ONLY on a definitive failure**, never on "inconclusive." States: `Ok` (passed), `Fail` (definitively broken → blocks), `Warn` (could not confirm — does NOT block), `Skip` (not applicable, e.g. reasoning disabled by config). `VerifyOutcome.Passed` is `gates.All(g => g.State != Fail)`.
- **Brain gate is log-marker based and async-aware:** the brain lazy-loads and its native libs may download in the background, so "not loaded yet" is NOT a failure. The brain gate `Fail`s ONLY when the Core log contains a definitive failure marker (`"model load failed"`, `NativeApi`, `TypeInitializationException`, or `"missing required native libs"`); it is `Ok` on `"model loaded in"`; `Skip` on `"Tier-2 LocalInference disabled"`; `Warn` if none of these appear within the poll window.
- **Generous timeouts on a 2-core box:** services-running wait ≤ 30s; pipe ping connect ≤ 5s; brain-log poll ≤ 20s. Never an unbounded wait.
- **Exact local paths:** Core log = newest file matching `%PROGRAMDATA%\SuavoAgent\logs\core-*.log`; cloud-auth health = `%PROGRAMDATA%\SuavoAgent\cloud-auth-health.json`; pipe nonce = `%PROGRAMDATA%\SuavoAgent\pipe.nonce`; the command pipe is named `SuavoAgent-cmd-{nonce}`. Use `Environment.SpecialFolder.CommonApplicationData` + `"SuavoAgent"`.
- **Reuse, don't duplicate:** the GateState/detail tuple shape and the existing `ConsoleUI`/`IInstallReporter`/phase conventions; do not re-implement service queries that `ServiceInstaller` already has.
- All new probe/verifier code lives under `src/SuavoAgent.Setup/Verify/`; tests under `tests/SuavoAgent.Setup.Tests/Verify/`. File-scoped namespaces.

---

### Task 1: Gate result types + `CloudAuthHealthProbe`

**Files:**
- Create: `src/SuavoAgent.Setup/Verify/VerifyGate.cs` (shared `GateState` enum + `GateResult` record)
- Create: `src/SuavoAgent.Setup/Verify/CloudAuthHealthProbe.cs`
- Test: `tests/SuavoAgent.Setup.Tests/Verify/CloudAuthHealthProbeTests.cs`

**Interfaces:**
- Produces: `enum GateState { Ok, Fail, Warn, Skip }`; `record GateResult(string Name, GateState State, string Detail)`. `CloudAuthHealthProbe(Func<string?> readHealthJson)` with `GateResult Check()` → reads the `cloud-auth-health.json` text; `status=="ok"` → `Ok`; an auth error kind (`lastErrorKind` containing `401` or `agent_not_found`, case-insensitive) → `Fail` with the error; missing/unreadable file → `Warn` ("cloud auth status not yet written").

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SuavoAgent.Setup.Tests/Verify/CloudAuthHealthProbeTests.cs
using SuavoAgent.Setup.Verify;
using Xunit;

namespace SuavoAgent.Setup.Tests.Verify;

public class CloudAuthHealthProbeTests
{
    private static GateResult Run(string? json) =>
        new CloudAuthHealthProbe(() => json).Check();

    [Fact]
    public void Status_ok_is_Ok()
    {
        var r = Run("{\"status\":\"ok\",\"lastSuccessAt\":\"2026-06-20T10:00:00Z\",\"lastErrorKind\":null}");
        Assert.Equal(GateState.Ok, r.State);
    }

    [Fact]
    public void Auth_error_kind_is_Fail()
    {
        var r = Run("{\"status\":\"failed\",\"lastErrorKind\":\"401_unauthorized\"}");
        Assert.Equal(GateState.Fail, r.State);
        Assert.Contains("401", r.Detail);
    }

    [Fact]
    public void Missing_file_is_Warn_not_Fail()
    {
        var r = Run(null);
        Assert.Equal(GateState.Warn, r.State);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SuavoAgent.Setup.Tests/SuavoAgent.Setup.Tests.csproj --filter "FullyQualifiedName~CloudAuthHealthProbeTests"`
Expected: FAIL (types missing).

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/SuavoAgent.Setup/Verify/VerifyGate.cs
namespace SuavoAgent.Setup.Verify;

/// <summary>Outcome of one self-verify gate. Fail blocks Success; Warn/Skip do not.</summary>
public enum GateState { Ok, Fail, Warn, Skip }

public sealed record GateResult(string Name, GateState State, string Detail);
```

```csharp
// src/SuavoAgent.Setup/Verify/CloudAuthHealthProbe.cs
using System;
using System.IO;
using System.Text.Json;

namespace SuavoAgent.Setup.Verify;

/// <summary>Reads cloud-auth-health.json: status "ok" passes; a 401 / agent_not_found error kind fails.</summary>
public sealed class CloudAuthHealthProbe
{
    private readonly Func<string?> _readHealthJson;

    public CloudAuthHealthProbe(Func<string?>? readHealthJson = null)
        => _readHealthJson = readHealthJson ?? ReadDefault;

    public GateResult Check()
    {
        var json = _readHealthJson();
        if (string.IsNullOrWhiteSpace(json))
            return new GateResult("Cloud auth", GateState.Warn, "Cloud auth status not yet written");
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var status = root.TryGetProperty("status", out var s) ? s.GetString() : null;
            var errKind = root.TryGetProperty("lastErrorKind", out var e) && e.ValueKind == JsonValueKind.String
                ? e.GetString() : null;
            if (!string.IsNullOrEmpty(errKind) &&
                (errKind.Contains("401", StringComparison.OrdinalIgnoreCase) ||
                 errKind.Contains("agent_not_found", StringComparison.OrdinalIgnoreCase)))
                return new GateResult("Cloud auth", GateState.Fail, $"Cloud auth failing: {errKind}");
            if (string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
                return new GateResult("Cloud auth", GateState.Ok, "Cloud auth healthy");
            return new GateResult("Cloud auth", GateState.Warn, $"Cloud auth status: {status ?? "unknown"}");
        }
        catch
        {
            return new GateResult("Cloud auth", GateState.Warn, "Cloud auth status unreadable");
        }
    }

    private static string? ReadDefault()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SuavoAgent", "cloud-auth-health.json");
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch { return null; }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/SuavoAgent.Setup.Tests/SuavoAgent.Setup.Tests.csproj --filter "FullyQualifiedName~CloudAuthHealthProbeTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/SuavoAgent.Setup/Verify/VerifyGate.cs src/SuavoAgent.Setup/Verify/CloudAuthHealthProbe.cs tests/SuavoAgent.Setup.Tests/Verify/CloudAuthHealthProbeTests.cs
git commit -m "feat(setup): self-verify gate types + CloudAuthHealthProbe"
```

---

### Task 2: `BrainHealthProbe` — classify the Core log

**Files:**
- Create: `src/SuavoAgent.Setup/Verify/BrainHealthProbe.cs`
- Test: `tests/SuavoAgent.Setup.Tests/Verify/BrainHealthProbeTests.cs`

**Interfaces:**
- Consumes: `GateResult`/`GateState` (Task 1).
- Produces: `BrainHealthProbe(Func<string?> readCoreLog)` with `GateResult Check()`. Classification priority (first match wins): a failure marker (`"model load failed"`, `"NativeApi"`, `"TypeInitializationException"`, `"missing required native libs"`) → `Fail` with VC++/native remediation; `"model loaded in"` → `Ok`; `"Tier-2 LocalInference disabled"` → `Skip`; `"Tier-2 LocalInference ENABLED"` (and no load result yet) → `Ok` ("brain provisioned; loads on first use"); none → `Warn`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SuavoAgent.Setup.Tests/Verify/BrainHealthProbeTests.cs
using SuavoAgent.Setup.Verify;
using Xunit;

namespace SuavoAgent.Setup.Tests.Verify;

public class BrainHealthProbeTests
{
    private static GateResult Run(string? log) => new BrainHealthProbe(() => log).Check();

    [Fact]
    public void Native_load_failure_is_Fail_with_remediation()
    {
        var r = Run("INF Tier-2 LocalInference ENABLED\nERR LLamaLocalInference: model load failed\nNativeApi threw");
        Assert.Equal(GateState.Fail, r.State);
        Assert.Contains("VC++", r.Detail);
    }

    [Fact]
    public void Model_loaded_is_Ok()
    {
        var r = Run("INF LLamaLocalInference: model loaded in 716ms (qwen3-1.7b)");
        Assert.Equal(GateState.Ok, r.State);
    }

    [Fact]
    public void Reasoning_disabled_is_Skip()
    {
        var r = Run("INF Tier-2 LocalInference disabled (Reasoning.Enabled=false) — running rules-only");
        Assert.Equal(GateState.Skip, r.State);
    }

    [Fact]
    public void Enabled_but_not_yet_loaded_is_Ok_provisioned()
    {
        var r = Run("INF Tier-2 LocalInference ENABLED — model 'qwen3-1.7b' (deferred: provisioning if absent)");
        Assert.Equal(GateState.Ok, r.State);
    }

    [Fact]
    public void No_markers_is_Warn()
    {
        var r = Run("INF some unrelated startup line");
        Assert.Equal(GateState.Warn, r.State);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SuavoAgent.Setup.Tests/SuavoAgent.Setup.Tests.csproj --filter "FullyQualifiedName~BrainHealthProbeTests"`
Expected: FAIL.

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/SuavoAgent.Setup/Verify/BrainHealthProbe.cs
using System;
using System.IO;
using System.Linq;

namespace SuavoAgent.Setup.Verify;

/// <summary>
/// Classifies the on-device brain from the Core log. Fails ONLY on a definitive load-failure marker
/// (the Nadim native-lib brick); "enabled but not yet loaded" is Ok (lazy load), disabled is Skip.
/// </summary>
public sealed class BrainHealthProbe
{
    private static readonly string[] FailureMarkers =
        { "model load failed", "NativeApi", "TypeInitializationException", "missing required native libs" };

    private readonly Func<string?> _readCoreLog;

    public BrainHealthProbe(Func<string?>? readCoreLog = null)
        => _readCoreLog = readCoreLog ?? ReadNewestCoreLog;

    public GateResult Check()
    {
        var log = _readCoreLog();
        if (string.IsNullOrEmpty(log))
            return new GateResult("Brain", GateState.Warn, "Brain status not yet logged");

        if (FailureMarkers.Any(m => log.Contains(m, StringComparison.OrdinalIgnoreCase)))
            return new GateResult("Brain", GateState.Fail,
                "On-device brain failed to load (native library). Ensure the VC++ 2015-2022 x64 Redistributable is installed, then restart the Core service.");
        if (log.Contains("model loaded in", StringComparison.OrdinalIgnoreCase))
            return new GateResult("Brain", GateState.Ok, "Brain loaded");
        if (log.Contains("Tier-2 LocalInference disabled", StringComparison.OrdinalIgnoreCase))
            return new GateResult("Brain", GateState.Skip, "Reasoning disabled by config");
        if (log.Contains("Tier-2 LocalInference ENABLED", StringComparison.OrdinalIgnoreCase))
            return new GateResult("Brain", GateState.Ok, "Brain provisioned; loads on first use");
        return new GateResult("Brain", GateState.Warn, "Brain status inconclusive");
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

Run: `dotnet test tests/SuavoAgent.Setup.Tests/SuavoAgent.Setup.Tests.csproj --filter "FullyQualifiedName~BrainHealthProbeTests"`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add src/SuavoAgent.Setup/Verify/BrainHealthProbe.cs tests/SuavoAgent.Setup.Tests/Verify/BrainHealthProbeTests.cs
git commit -m "feat(setup): BrainHealthProbe — classify Core log (fail only on definitive native-load failure)"
```

---

### Task 3: `PipePingProbe` — confirm the Core↔Helper pipe

**Files:**
- Create: `src/SuavoAgent.Setup/Verify/PipePingProbe.cs`
- Test: `tests/SuavoAgent.Setup.Tests/Verify/PipePingProbeTests.cs`

**Interfaces:**
- Consumes: `GateResult`/`GateState` (Task 1).
- Produces: `PipePingProbe(Func<string?> readNonce, Func<string, CancellationToken, Task<bool>> tryConnect)` with `async Task<GateResult> CheckAsync(CancellationToken ct)`. Missing nonce → `Warn` ("agent not started yet"); connect to `SuavoAgent-cmd-{nonce}` succeeds → `Ok`; fails → `Fail` ("command pipe unreachable"). Pure logic is the nonce→pipe-name mapping and outcome classification; the actual `NamedPipeClientStream` connect is the injected `tryConnect`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SuavoAgent.Setup.Tests/Verify/PipePingProbeTests.cs
using System.Threading;
using System.Threading.Tasks;
using SuavoAgent.Setup.Verify;
using Xunit;

namespace SuavoAgent.Setup.Tests.Verify;

public class PipePingProbeTests
{
    [Fact]
    public async Task Missing_nonce_is_Warn()
    {
        var probe = new PipePingProbe(() => null, (_, _) => Task.FromResult(true));
        var r = await probe.CheckAsync(CancellationToken.None);
        Assert.Equal(GateState.Warn, r.State);
    }

    [Fact]
    public async Task Connect_success_is_Ok_and_uses_cmd_pipe_name()
    {
        string? attempted = null;
        var probe = new PipePingProbe(() => "abc123", (name, _) => { attempted = name; return Task.FromResult(true); });
        var r = await probe.CheckAsync(CancellationToken.None);
        Assert.Equal(GateState.Ok, r.State);
        Assert.Equal("SuavoAgent-cmd-abc123", attempted);
    }

    [Fact]
    public async Task Connect_failure_is_Fail()
    {
        var probe = new PipePingProbe(() => "abc123", (_, _) => Task.FromResult(false));
        var r = await probe.CheckAsync(CancellationToken.None);
        Assert.Equal(GateState.Fail, r.State);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SuavoAgent.Setup.Tests/SuavoAgent.Setup.Tests.csproj --filter "FullyQualifiedName~PipePingProbeTests"`
Expected: FAIL.

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/SuavoAgent.Setup/Verify/PipePingProbe.cs
using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace SuavoAgent.Setup.Verify;

/// <summary>Confirms the Core command pipe (SuavoAgent-cmd-{nonce}) is reachable — proof Core is up and serving.</summary>
public sealed class PipePingProbe
{
    private readonly Func<string?> _readNonce;
    private readonly Func<string, CancellationToken, Task<bool>> _tryConnect;

    public PipePingProbe(
        Func<string?>? readNonce = null,
        Func<string, CancellationToken, Task<bool>>? tryConnect = null)
    {
        _readNonce = readNonce ?? ReadNonce;
        _tryConnect = tryConnect ?? TryConnectReal;
    }

    public async Task<GateResult> CheckAsync(CancellationToken ct)
    {
        var nonce = _readNonce()?.Trim();
        if (string.IsNullOrEmpty(nonce))
            return new GateResult("Pipe", GateState.Warn, "Agent pipe not advertised yet");
        var pipeName = $"SuavoAgent-cmd-{nonce}";
        var ok = await _tryConnect(pipeName, ct);
        return ok
            ? new GateResult("Pipe", GateState.Ok, "Core command pipe reachable")
            : new GateResult("Pipe", GateState.Fail, "Core command pipe unreachable");
    }

    private static string? ReadNonce()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SuavoAgent", "pipe.nonce");
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch { return null; }
    }

    private static async Task<bool> TryConnectReal(string pipeName, CancellationToken ct)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
            await pipe.ConnectAsync(TimeSpan.FromSeconds(5), ct);
            return pipe.IsConnected;
        }
        catch { return false; }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/SuavoAgent.Setup.Tests/SuavoAgent.Setup.Tests.csproj --filter "FullyQualifiedName~PipePingProbeTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/SuavoAgent.Setup/Verify/PipePingProbe.cs tests/SuavoAgent.Setup.Tests/Verify/PipePingProbeTests.cs
git commit -m "feat(setup): PipePingProbe — verify Core command pipe via nonce"
```

---

### Task 4: `PostInstallVerifier` — compose gates + write `install-verify.json`

**Files:**
- Create: `src/SuavoAgent.Setup/Verify/PostInstallVerifier.cs`
- Test: `tests/SuavoAgent.Setup.Tests/Verify/PostInstallVerifierTests.cs`

**Interfaces:**
- Consumes: `GateResult`/`GateState` (Task 1).
- Produces: `PostInstallVerifier(IReadOnlyList<Func<CancellationToken,Task<GateResult>>> gates)` with `async Task<VerifyOutcome> RunAsync(CancellationToken ct)` returning `record VerifyOutcome(bool Passed, IReadOnlyList<GateResult> Gates, string Summary)`. `Passed = Gates.All(g => g.State != GateState.Fail)`. `Summary` names the first failing gate + its detail when failed, else "All checks passed." A static `string ToJson(VerifyOutcome)` serializes the outcome for `install-verify.json`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SuavoAgent.Setup.Tests/Verify/PostInstallVerifierTests.cs
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SuavoAgent.Setup.Verify;
using Xunit;

namespace SuavoAgent.Setup.Tests.Verify;

public class PostInstallVerifierTests
{
    private static Func<CancellationToken, Task<GateResult>> Gate(string name, GateState s) =>
        _ => Task.FromResult(new GateResult(name, s, $"{name} {s}"));

    [Fact]
    public async Task Passes_when_no_gate_fails()
    {
        var v = new PostInstallVerifier(new[] { Gate("Services", GateState.Ok), Gate("Brain", GateState.Skip), Gate("Cloud", GateState.Warn) });
        var outcome = await v.RunAsync(CancellationToken.None);
        Assert.True(outcome.Passed);
        Assert.Equal(3, outcome.Gates.Count);
    }

    [Fact]
    public async Task Fails_and_summary_names_first_failing_gate()
    {
        var v = new PostInstallVerifier(new[] { Gate("Services", GateState.Ok), Gate("Brain", GateState.Fail) });
        var outcome = await v.RunAsync(CancellationToken.None);
        Assert.False(outcome.Passed);
        Assert.Contains("Brain", outcome.Summary);
    }

    [Fact]
    public void ToJson_includes_each_gate_and_passed_flag()
    {
        var outcome = new VerifyOutcome(false,
            new[] { new GateResult("Brain", GateState.Fail, "broken") }, "Brain: broken");
        var json = PostInstallVerifier.ToJson(outcome);
        Assert.Contains("\"passed\"", json);
        Assert.Contains("Brain", json);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SuavoAgent.Setup.Tests/SuavoAgent.Setup.Tests.csproj --filter "FullyQualifiedName~PostInstallVerifierTests"`
Expected: FAIL.

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/SuavoAgent.Setup/Verify/PostInstallVerifier.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SuavoAgent.Setup.Verify;

/// <summary>Runs all self-verify gates and produces a pass/fail outcome. Fail on any gate blocks Success.</summary>
public sealed class PostInstallVerifier
{
    private readonly IReadOnlyList<Func<CancellationToken, Task<GateResult>>> _gates;

    public PostInstallVerifier(IReadOnlyList<Func<CancellationToken, Task<GateResult>>> gates)
        => _gates = gates;

    public async Task<VerifyOutcome> RunAsync(CancellationToken ct)
    {
        var results = new List<GateResult>();
        foreach (var gate in _gates)
            results.Add(await gate(ct));
        var firstFail = results.FirstOrDefault(g => g.State == GateState.Fail);
        var passed = firstFail is null;
        var summary = passed ? "All checks passed." : $"{firstFail!.Name}: {firstFail.Detail}";
        return new VerifyOutcome(passed, results, summary);
    }

    public static string ToJson(VerifyOutcome outcome) => JsonSerializer.Serialize(new
    {
        passed = outcome.Passed,
        summary = outcome.Summary,
        gates = outcome.Gates.Select(g => new { name = g.Name, state = g.State.ToString(), detail = g.Detail }),
    }, new JsonSerializerOptions { WriteIndented = true });
}

public sealed record VerifyOutcome(bool Passed, IReadOnlyList<GateResult> Gates, string Summary);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/SuavoAgent.Setup.Tests/SuavoAgent.Setup.Tests.csproj --filter "FullyQualifiedName~PostInstallVerifierTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/SuavoAgent.Setup/Verify/PostInstallVerifier.cs tests/SuavoAgent.Setup.Tests/Verify/PostInstallVerifierTests.cs
git commit -m "feat(setup): PostInstallVerifier — compose gates, write install-verify.json"
```

---

### Task 5: Expose a services-running gate on `ServiceInstaller`

**Files:**
- Modify: `src/SuavoAgent.Setup/ServiceInstaller.cs` (add a public `GateResult ServicesRunningGate()` that reuses the existing private `IsServiceRunning` + `WaitForHelperProcess`)
- Test: `tests/SuavoAgent.Setup.Tests/Verify/ServicesRunningGateTests.cs` (test the classification with injected service-state)

**Interfaces:**
- Consumes: `GateResult`/`GateState` (Task 1), existing `ServiceInstaller.IsServiceRunning`/`WaitForHelperProcess`.
- Produces: a testable static `GateResult ServiceInstaller.ClassifyServices(bool core, bool broker, bool watchdog, bool helper)` → `Fail` (with which one) if Core/Broker/Helper missing; Watchdog-missing → `Warn`; all present → `Ok`. Plus a public `GateResult ServicesRunningGate()` that calls the real probes and `ClassifyServices`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SuavoAgent.Setup.Tests/Verify/ServicesRunningGateTests.cs
using SuavoAgent.Setup;
using SuavoAgent.Setup.Verify;
using Xunit;

namespace SuavoAgent.Setup.Tests.Verify;

public class ServicesRunningGateTests
{
    [Fact]
    public void All_running_is_Ok()
        => Assert.Equal(GateState.Ok, ServiceInstaller.ClassifyServices(true, true, true, true).State);

    [Fact]
    public void Core_down_is_Fail()
    {
        var r = ServiceInstaller.ClassifyServices(false, true, true, true);
        Assert.Equal(GateState.Fail, r.State);
        Assert.Contains("Core", r.Detail);
    }

    [Fact]
    public void Watchdog_down_is_Warn_not_Fail()
        => Assert.Equal(GateState.Warn, ServiceInstaller.ClassifyServices(true, true, false, true).State);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SuavoAgent.Setup.Tests/SuavoAgent.Setup.Tests.csproj --filter "FullyQualifiedName~ServicesRunningGateTests"`
Expected: FAIL (`ClassifyServices` missing).

- [ ] **Step 3: Write minimal implementation**

Read `ServiceInstaller.cs` first to match its style and confirm `IsServiceRunning`/`WaitForHelperProcess` names/signatures. Then add:

```csharp
// in src/SuavoAgent.Setup/ServiceInstaller.cs (add these members; using SuavoAgent.Setup.Verify;)
public static GateResult ClassifyServices(bool core, bool broker, bool watchdog, bool helper)
{
    if (!core) return new GateResult("Services", GateState.Fail, "Core service not running");
    if (!broker) return new GateResult("Services", GateState.Fail, "Broker service not running");
    if (!helper) return new GateResult("Services", GateState.Fail, "Helper process not running");
    if (!watchdog) return new GateResult("Services", GateState.Warn, "Watchdog not running yet");
    return new GateResult("Services", GateState.Ok, "All services running");
}

public static GateResult ServicesRunningGate() => ClassifyServices(
    IsServiceRunning("SuavoAgent.Core"),
    IsServiceRunning("SuavoAgent.Broker"),
    IsServiceRunning("SuavoAgent.Watchdog"),
    WaitForHelperProcess(System.TimeSpan.FromSeconds(30)));
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/SuavoAgent.Setup.Tests/SuavoAgent.Setup.Tests.csproj --filter "FullyQualifiedName~ServicesRunningGateTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/SuavoAgent.Setup/ServiceInstaller.cs tests/SuavoAgent.Setup.Tests/Verify/ServicesRunningGateTests.cs
git commit -m "feat(setup): ServiceInstaller services-running gate for self-verify"
```

---

### Task 6: Wire the Verify phase into the GUI (`InstallOrchestrator` + `ProgressViewModel`)

**Files:**
- Modify: `src/SuavoAgent.Setup/Gui/Services/InstallOrchestrator.cs` (add `Verify` to the `Phase` enum; run `PostInstallVerifier` after `InstallServices`, before `Phase.Done`; on `!Passed` throw `InstallException` with the failing-gate summary; write `install-verify.json` to the data dir)
- Modify: `src/SuavoAgent.Setup/Gui/ViewModels/ProgressViewModel.cs` (add the 5th phase title "Verify installation")
- Modify: `tests/SuavoAgent.Setup.Tests/ProgressViewModelTests.cs` (update the phase-count assertion 4 → 5)
- Test: `tests/SuavoAgent.Setup.Tests/Verify/InstallOrchestratorVerifyTests.cs` (assert a failing verify blocks completion)

**Interfaces:**
- Consumes: `PostInstallVerifier` (T4), `ServiceInstaller.ServicesRunningGate` (T5), `BrainHealthProbe`/`PipePingProbe`/`CloudAuthHealthProbe` (T1–T3).
- Produces: the orchestrator composes the four real gates into a `PostInstallVerifier`, runs it as the `Verify` phase, writes `install-verify.json`, and aborts (throws `InstallException`) on failure so `GoToSuccess()` is never reached. INTEGRATION — read the existing files.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SuavoAgent.Setup.Tests/Verify/InstallOrchestratorVerifyTests.cs
using System.Linq;
using SuavoAgent.Setup.Gui.Services;
using Xunit;

namespace SuavoAgent.Setup.Tests.Verify;

public class InstallOrchestratorVerifyTests
{
    [Fact]
    public void Phase_enum_has_Verify_before_Done()
    {
        Assert.True((int)InstallOrchestrator.Phase.Verify < (int)InstallOrchestrator.Phase.Done);
        Assert.True((int)InstallOrchestrator.Phase.InstallServices < (int)InstallOrchestrator.Phase.Verify);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SuavoAgent.Setup.Tests/SuavoAgent.Setup.Tests.csproj --filter "FullyQualifiedName~InstallOrchestratorVerifyTests"`
Expected: FAIL (`Phase.Verify` missing).

- [ ] **Step 3: Write minimal implementation**

Read `InstallOrchestrator.cs` and `ProgressViewModel.cs` in full first. Then:
1. `InstallOrchestrator.Phase` enum → `{ Download=0, WriteConfig=1, InstallBrain=2, InstallServices=3, Verify=4, Done }`.
2. After the `InstallServices` success check and before `progress.Report(new PhaseEvent(Phase.Done, ...))`, insert:
```csharp
progress.Report(new PhaseEvent(Phase.Verify, "Verifying installation"));
ConsoleUI.WriteStep("Phase 7: Verifying installation");
var verifier = new PostInstallVerifier(new Func<CancellationToken, Task<GateResult>>[]
{
    _ => Task.FromResult(ServiceInstaller.ServicesRunningGate()),
    ct2 => new PipePingProbe().CheckAsync(ct2),
    _ => Task.FromResult(new BrainHealthProbe().Check()),
    _ => Task.FromResult(new CloudAuthHealthProbe().Check()),
});
var outcome = await verifier.RunAsync(ct);
try
{
    File.WriteAllText(
        Path.Combine(_ctx.DataDir, "install-verify.json"), PostInstallVerifier.ToJson(outcome));
}
catch { /* best-effort forensic artifact */ }
if (!outcome.Passed)
    throw new InstallException($"Post-install verification failed — {outcome.Summary}. Details: {SetupLog.LogPath}");
```
3. `ProgressViewModel` phase titles array: append `"Verify installation"` (so 5 titles).
4. `ProgressViewModelTests`: update the phase-count assertion from `4` to `5` (and any hard-coded phase-index references for the trailing phase).

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SuavoAgent.Setup.Tests/SuavoAgent.Setup.Tests.csproj --filter "FullyQualifiedName~InstallOrchestratorVerifyTests|FullyQualifiedName~ProgressViewModelTests"`
Expected: PASS (the new test + all ProgressViewModel tests).

- [ ] **Step 5: Commit**

```bash
git add src/SuavoAgent.Setup/Gui/Services/InstallOrchestrator.cs src/SuavoAgent.Setup/Gui/ViewModels/ProgressViewModel.cs tests/SuavoAgent.Setup.Tests/ProgressViewModelTests.cs tests/SuavoAgent.Setup.Tests/Verify/InstallOrchestratorVerifyTests.cs
git commit -m "feat(setup): GUI Verify phase — self-verify gates the Success screen + writes install-verify.json"
```

---

### Task 7: Wire self-verify into the headless `ConsoleInstaller`

**Files:**
- Modify: `src/SuavoAgent.Setup/ConsoleInstaller.cs` (after the service-start phase, before the completion summary: run the same `PostInstallVerifier`, write `install-verify.json`, and on `!Passed` render the failing-gate summary + `return 1`)
- Test: `tests/SuavoAgent.Setup.Tests/Verify/ConsoleVerifyWiringTests.cs` (a lightweight guard that the verifier composition helper exists and composes 4 gates)

**Interfaces:**
- Consumes: `PostInstallVerifier` (T4) + the four gates (T1–T3, T5).
- Produces: extract the four-gate composition into a small internal static helper `BuildDefaultVerifier()` (in `PostInstallVerifier` or a `VerifierFactory`) so BOTH the GUI orchestrator and the console path build the identical gate set (DRY). Console aborts on `!Passed` with `return 1`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SuavoAgent.Setup.Tests/Verify/ConsoleVerifyWiringTests.cs
using System.Threading;
using System.Threading.Tasks;
using SuavoAgent.Setup.Verify;
using Xunit;

namespace SuavoAgent.Setup.Tests.Verify;

public class ConsoleVerifyWiringTests
{
    [Fact]
    public async Task Default_verifier_composes_four_named_gates()
    {
        // The default production verifier must run exactly the four gates: Services, Pipe, Brain, Cloud auth.
        var outcome = await VerifierFactory.BuildDefault().RunAsync(CancellationToken.None);
        var names = new System.Collections.Generic.HashSet<string>();
        foreach (var g in outcome.Gates) names.Add(g.Name);
        Assert.Contains("Services", names);
        Assert.Contains("Pipe", names);
        Assert.Contains("Brain", names);
        Assert.Contains("Cloud auth", names);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SuavoAgent.Setup.Tests/SuavoAgent.Setup.Tests.csproj --filter "FullyQualifiedName~ConsoleVerifyWiringTests"`
Expected: FAIL (`VerifierFactory` missing). Note: on a non-Windows/CI box the real gates return Warn/Fail (no services/pipe), which is fine — the test only asserts the four gate NAMES are present, not their states.

- [ ] **Step 3: Write minimal implementation**

1. Create `src/SuavoAgent.Setup/Verify/VerifierFactory.cs`:
```csharp
using System;
using System.Threading;
using System.Threading.Tasks;

namespace SuavoAgent.Setup.Verify;

/// <summary>Single source of the production gate set so GUI + console verify identically.</summary>
public static class VerifierFactory
{
    public static PostInstallVerifier BuildDefault() => new(new Func<CancellationToken, Task<GateResult>>[]
    {
        _ => Task.FromResult(ServiceInstaller.ServicesRunningGate()),
        ct => new PipePingProbe().CheckAsync(ct),
        _ => Task.FromResult(new BrainHealthProbe().Check()),
        _ => Task.FromResult(new CloudAuthHealthProbe().Check()),
    });
}
```
2. Refactor Task 6's GUI orchestrator to call `VerifierFactory.BuildDefault()` (DRY).
3. In `ConsoleInstaller.RunAsync`, after the service-start phase and before the completion summary, add:
```csharp
ConsoleUI.WriteStep("Phase 7: Verifying installation");
var verifyOutcome = await VerifierFactory.BuildDefault().RunAsync(ct);
try { File.WriteAllText(Path.Combine(dataDir, "install-verify.json"), PostInstallVerifier.ToJson(verifyOutcome)); } catch { }
if (!verifyOutcome.Passed)
{
    ConsoleUI.FatalError($"Post-install verification failed — {verifyOutcome.Summary}");
    return 1;
}
ConsoleUI.WriteOk($"Verification passed: {verifyOutcome.Summary}");
```
(Match the existing `ConsoleUI`/`dataDir`/return-code conventions — read the file.)

- [ ] **Step 4: Run the FULL Setup suite**

Run: `dotnet test tests/SuavoAgent.Setup.Tests/SuavoAgent.Setup.Tests.csproj`
Expected: PASS (all Setup tests, including every new Verify test).

- [ ] **Step 5: Commit**

```bash
git add src/SuavoAgent.Setup/Verify/VerifierFactory.cs src/SuavoAgent.Setup/Gui/Services/InstallOrchestrator.cs src/SuavoAgent.Setup/ConsoleInstaller.cs tests/SuavoAgent.Setup.Tests/Verify/ConsoleVerifyWiringTests.cs
git commit -m "feat(setup): headless console self-verify + shared VerifierFactory (GUI+console parity)"
```

---

## Self-Review

**Spec coverage (vs Phase B of the installer-preflight-self-verify spec):**
- ✅ Services-running gate (T5), Core↔Helper pipe ping (T3), brain-loads gate (T2, log-marker based — fails only on the definitive native-load failure that was the Nadim brick), cloud-auth gate (T1).
- ✅ Gates compose into a verifier that blocks the Success screen (T6) and the console completion (T7); writes `install-verify.json` (T4/T6/T7).
- ✅ Fail blocks; Warn/Skip/inconclusive do not (Global Constraints; tested in T1–T4).
- ⏳ **Deferred (noted, not dropped):** PMS-reachability gate (modality-aware UiaFirst "can see PioneerRx") — lower priority + risks touching the PMS; a follow-on. Dashboard surfacing of `install-verify.json` is **Phase C** (separate plan). Forcing an actual one-token brain inference (vs. log-marker classification) is a stretch that needs a local Core inference IPC that does not yet exist — deferred to the "Suavo doctor" feature.

**Placeholder scan:** none — every step has complete code or an explicit "read the file then apply this" for the two integration tasks (T6, T7), with the exact insertion code given.

**Type consistency:** `GateState{Ok,Fail,Warn,Skip}` + `GateResult(Name,State,Detail)` (T1) used identically across T2–T7. `VerifyOutcome(Passed,Gates,Summary)` + `PostInstallVerifier.ToJson` (T4) used in T6/T7. `ServiceInstaller.ServicesRunningGate()`/`ClassifyServices` (T5), `VerifierFactory.BuildDefault()` (T7) referenced consistently. Brain failure markers list matches the Core log strings from the grounding map.

**Note for the implementer:** Read `InstallOrchestrator.cs`, `ProgressViewModel.cs`, and `ConsoleInstaller.cs` in full before T6/T7 — match their exact `PhaseEvent`/`ConsoleUI`/`InstallException`/`dataDir` shapes rather than the illustrative snippets. The brain gate must NEVER `Fail` on "not loaded yet" — only on a definitive failure marker — or it will false-positive every install where the brain hasn't been exercised yet.
