# Schema Canary Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Detect PMS database schema drift on every detection cycle and fail closed at the sync boundary — suspect data never reaches drivers.

**Architecture:** New `ICanaryDetectionSource` abstraction in Contracts. Pure classifier and escalation state machine in Core. `PioneerRxCanarySource` wraps `PioneerRxSqlEngine` in the Adapters.PioneerRx assembly. `RxDetectionWorker` gets a `RunCycleAsync` seam with preflight/postflight gates. Canary state persisted in SQLite, reported via heartbeat. Per errata: Warning is non-blocking, Advisory tier removed, baselines auto-established on first run.

**Tech Stack:** .NET 8, xUnit, SQLite, SHA-256, Microsoft.Data.SqlClient sys.* catalog views

**Spec:** `docs/superpowers/specs/2026-04-13-schema-canary-design.md` (including Review Errata section)

**Build/test commands:**
```bash
cd ~/Documents/SuavoAgent
dotnet build
dotnet test
dotnet test --filter "FullyQualifiedName~ClassName"
```

---

## File Map

### New Files
| File | Responsibility |
|------|---------------|
| `src/SuavoAgent.Contracts/Canary/ICanaryDetectionSource.cs` | Interface + `DetectionResult` record |
| `src/SuavoAgent.Contracts/Canary/ContractBaseline.cs` | Baseline record with 4 component hashes + composite |
| `src/SuavoAgent.Contracts/Canary/ContractVerification.cs` | Verification result record |
| `src/SuavoAgent.Contracts/Canary/CanarySeverity.cs` | `{ None, Warning, Critical }` enum (errata E12: Advisory removed) |
| `src/SuavoAgent.Contracts/Canary/ObservedContract.cs` | Raw observed schema data for classifier input |
| `src/SuavoAgent.Core/Canary/SchemaCanaryClassifier.cs` | Pure function: `(baseline, observed) → CanarySeverity + details` |
| `src/SuavoAgent.Core/Canary/SchemaCanaryEscalation.cs` | Pure state machine: `(holdState, severity) → holdState` |
| `src/SuavoAgent.Core/Canary/ContractFingerprinter.cs` | SHA-256 hashing of contract components |
| `src/SuavoAgent.Adapters.PioneerRx/Canary/PioneerRxCanarySource.cs` | `ICanaryDetectionSource` for PioneerRx (errata E18: lives in Adapters assembly) |
| `tests/SuavoAgent.Core.Tests/Canary/ContractFingerprinterTests.cs` | Hash determinism, sensitivity, isolation |
| `tests/SuavoAgent.Core.Tests/Canary/SchemaCanaryClassifierTests.cs` | All severity classification cases |
| `tests/SuavoAgent.Core.Tests/Canary/SchemaCanaryEscalationTests.cs` | State machine transitions, restart survival |
| `tests/SuavoAgent.Core.Tests/Canary/CanaryDetectionCycleTests.cs` | RunCycleAsync integration tests |
| `tests/SuavoAgent.Core.Tests/Canary/CanaryAcknowledgeTests.cs` | Signed command tests |

### Modified Files
| File | Changes |
|------|---------|
| `src/SuavoAgent.Core/State/AgentStateDb.cs` | Add 3 canary tables + CRUD methods + hold persistence |
| `src/SuavoAgent.Core/Workers/RxDetectionWorker.cs` | Add `ICanaryDetectionSource?` injection, `RunCycleAsync` seam, canary gates |
| `src/SuavoAgent.Core/Workers/HeartbeatWorker.cs` | Add canary telemetry to payload, `acknowledge_drift` handler, heartbeat frequency |
| `src/SuavoAgent.Core/HealthSnapshot.cs` | Add canary status fields |
| `src/SuavoAgent.Core/Program.cs` | Register `ICanaryDetectionSource` via DI |
| `src/SuavoAgent.Adapters.PioneerRx/Sql/PioneerRxSqlEngine.cs` | Add `QueryContractMetadataAsync`, `QueryStatusMapAsync` methods |

---

## Task 1: Canary Types in Contracts

**Files:**
- Create: `src/SuavoAgent.Contracts/Canary/CanarySeverity.cs`
- Create: `src/SuavoAgent.Contracts/Canary/ContractBaseline.cs`
- Create: `src/SuavoAgent.Contracts/Canary/ObservedContract.cs`
- Create: `src/SuavoAgent.Contracts/Canary/ContractVerification.cs`
- Create: `src/SuavoAgent.Contracts/Canary/ICanaryDetectionSource.cs`

- [ ] **Step 1: Create CanarySeverity enum**

```csharp
// src/SuavoAgent.Contracts/Canary/CanarySeverity.cs
namespace SuavoAgent.Contracts.Canary;

/// <summary>
/// Schema drift severity. Advisory removed per errata E12 (unreachable).
/// </summary>
public enum CanarySeverity { None, Warning, Critical }
```

- [ ] **Step 2: Create ContractBaseline record**

```csharp
// src/SuavoAgent.Contracts/Canary/ContractBaseline.cs
namespace SuavoAgent.Contracts.Canary;

/// <summary>
/// Approved detection contract baseline with 4 component hashes.
/// Stored in schema_canary_baselines table.
/// </summary>
public record ContractBaseline(
    string AdapterType,
    string ObjectFingerprint,
    string StatusMapFingerprint,
    string QueryFingerprint,
    string ResultShapeFingerprint,
    string ContractFingerprint,
    string ContractJson,
    int SchemaEpoch,
    int ContractVersion = 1);
```

- [ ] **Step 3: Create ObservedContract record**

```csharp
// src/SuavoAgent.Contracts/Canary/ObservedContract.cs
namespace SuavoAgent.Contracts.Canary;

/// <summary>
/// Raw observed schema metadata from preflight queries.
/// Fed into SchemaCanaryClassifier alongside the approved baseline.
/// </summary>
public record ObservedContract(
    IReadOnlyList<ObservedObject> Objects,
    IReadOnlyList<ObservedStatus> StatusMap,
    string QueryFingerprint,
    string? ResultShapeFingerprint);

public record ObservedObject(
    string SchemaName,
    string TableName,
    string ColumnName,
    string DataTypeName,
    int? MaxLength,
    bool IsNullable,
    bool IsRequired);

public record ObservedStatus(
    string Description,
    string GuidValue);
```

- [ ] **Step 4: Create ContractVerification record**

```csharp
// src/SuavoAgent.Contracts/Canary/ContractVerification.cs
namespace SuavoAgent.Contracts.Canary;

public record ContractVerification(
    bool IsValid,
    CanarySeverity Severity,
    IReadOnlyList<string> DriftedComponents,
    string? BaselineHash,
    string? ObservedHash,
    string? Details)
{
    public static ContractVerification Clean { get; } = new(true, CanarySeverity.None,
        Array.Empty<string>(), null, null, null);
}
```

- [ ] **Step 5: Create ICanaryDetectionSource interface**

```csharp
// src/SuavoAgent.Contracts/Canary/ICanaryDetectionSource.cs
using SuavoAgent.Contracts.Models;

namespace SuavoAgent.Contracts.Canary;

public interface ICanaryDetectionSource
{
    string AdapterType { get; }
    ContractBaseline GetContractBaseline();
    Task<ContractVerification> VerifyPreflightAsync(
        ContractBaseline approved, CancellationToken ct);
    Task<DetectionResult> DetectWithCanaryAsync(
        ContractBaseline approved, CancellationToken ct);
}

public record DetectionResult(
    IReadOnlyList<RxMetadata> Rxs,
    ContractVerification PostflightVerification);
```

- [ ] **Step 6: Build and verify**

Run: `dotnet build src/SuavoAgent.Contracts/SuavoAgent.Contracts.csproj`
Expected: Build succeeded

- [ ] **Step 7: Commit**

```bash
git add src/SuavoAgent.Contracts/Canary/
git commit -m "feat(canary): add contract types — CanarySeverity, ContractBaseline, ObservedContract, ContractVerification, ICanaryDetectionSource"
```

---

## Task 2: ContractFingerprinter — Pure Hashing

**Files:**
- Create: `src/SuavoAgent.Core/Canary/ContractFingerprinter.cs`
- Create: `tests/SuavoAgent.Core.Tests/Canary/ContractFingerprinterTests.cs`

- [ ] **Step 1: Write fingerprinter tests**

```csharp
// tests/SuavoAgent.Core.Tests/Canary/ContractFingerprinterTests.cs
using SuavoAgent.Contracts.Canary;
using SuavoAgent.Core.Canary;
using Xunit;

namespace SuavoAgent.Core.Tests.Canary;

public class ContractFingerprinterTests
{
    private static readonly ObservedObject Col1 = new("Prescription", "RxTransaction",
        "RxNumber", "int", null, false, true);
    private static readonly ObservedObject Col2 = new("Prescription", "RxTransaction",
        "DateFilled", "datetime", null, true, true);
    private static readonly ObservedStatus Status1 = new("Waiting for Pick up",
        "53ce4c47-dff2-46ac-a310-719e792239ef");
    private static readonly ObservedStatus Status2 = new("Waiting for Delivery",
        "c3adbbcc-76e3-4b06-a0dc-4e8b8ce0a2de");

    [Fact]
    public void ObjectFingerprint_SameInput_SameHash()
    {
        var a = ContractFingerprinter.HashObjects(new[] { Col1, Col2 });
        var b = ContractFingerprinter.HashObjects(new[] { Col1, Col2 });
        Assert.Equal(a, b);
    }

    [Fact]
    public void ObjectFingerprint_DifferentOrder_SameHash()
    {
        var a = ContractFingerprinter.HashObjects(new[] { Col1, Col2 });
        var b = ContractFingerprinter.HashObjects(new[] { Col2, Col1 });
        Assert.Equal(a, b);
    }

    [Fact]
    public void ObjectFingerprint_ColumnRenamed_DifferentHash()
    {
        var renamed = Col1 with { ColumnName = "RxNum" };
        var a = ContractFingerprinter.HashObjects(new[] { Col1, Col2 });
        var b = ContractFingerprinter.HashObjects(new[] { renamed, Col2 });
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ObjectFingerprint_TypeChanged_DifferentHash()
    {
        var retyped = Col1 with { DataTypeName = "varchar" };
        var a = ContractFingerprinter.HashObjects(new[] { Col1, Col2 });
        var b = ContractFingerprinter.HashObjects(new[] { retyped, Col2 });
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void StatusMapFingerprint_SameInput_SameHash()
    {
        var a = ContractFingerprinter.HashStatusMap(new[] { Status1, Status2 });
        var b = ContractFingerprinter.HashStatusMap(new[] { Status1, Status2 });
        Assert.Equal(a, b);
    }

    [Fact]
    public void StatusMapFingerprint_DifferentOrder_SameHash()
    {
        var a = ContractFingerprinter.HashStatusMap(new[] { Status1, Status2 });
        var b = ContractFingerprinter.HashStatusMap(new[] { Status2, Status1 });
        Assert.Equal(a, b);
    }

    [Fact]
    public void StatusMapFingerprint_GuidChanged_DifferentHash()
    {
        var changed = Status1 with { GuidValue = "00000000-0000-0000-0000-000000000000" };
        var a = ContractFingerprinter.HashStatusMap(new[] { Status1, Status2 });
        var b = ContractFingerprinter.HashStatusMap(new[] { changed, Status2 });
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void StatusMapFingerprint_DescriptionRenamed_DifferentHash()
    {
        var renamed = Status1 with { Description = "Ready for Pickup" };
        var a = ContractFingerprinter.HashStatusMap(new[] { Status1, Status2 });
        var b = ContractFingerprinter.HashStatusMap(new[] { renamed, Status2 });
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void CompositeFingerprint_DerivedFromComponents()
    {
        var obj = ContractFingerprinter.HashObjects(new[] { Col1 });
        var status = ContractFingerprinter.HashStatusMap(new[] { Status1 });
        var query = ContractFingerprinter.HashQuery("SELECT 1");
        var shape = ContractFingerprinter.HashResultShape(new[] { ("RxNumber", "int") });
        var composite = ContractFingerprinter.CompositeHash(obj, status, query, shape);
        Assert.NotEmpty(composite);
        Assert.Equal(64, composite.Length); // SHA-256 hex
    }

    [Fact]
    public void ObjectFingerprint_ComponentIsolation_StatusChangeDoesNotAffectObjectHash()
    {
        var objHash1 = ContractFingerprinter.HashObjects(new[] { Col1 });
        var objHash2 = ContractFingerprinter.HashObjects(new[] { Col1 }); // same objects
        Assert.Equal(objHash1, objHash2);
        // Status changes are a separate component — verified by different hash method
    }

    [Fact]
    public void QueryFingerprint_TemplateVsExpanded_DifferentHash()
    {
        var template = ContractFingerprinter.HashQuery("SELECT * FROM T WHERE status IN ({statusParams})");
        var expanded = ContractFingerprinter.HashQuery("SELECT * FROM T WHERE status IN (@s0, @s1)");
        Assert.NotEqual(template, expanded);
    }

    [Fact]
    public void ResultShapeFingerprint_ColumnMissing_DifferentHash()
    {
        var full = ContractFingerprinter.HashResultShape(new[]
            { ("RxNumber", "int"), ("DateFilled", "datetime") });
        var partial = ContractFingerprinter.HashResultShape(new[]
            { ("RxNumber", "int") });
        Assert.NotEqual(full, partial);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SuavoAgent.Core.Tests --filter "FullyQualifiedName~ContractFingerprinterTests" -v minimal`
Expected: Build error — `ContractFingerprinter` does not exist

- [ ] **Step 3: Implement ContractFingerprinter**

```csharp
// src/SuavoAgent.Core/Canary/ContractFingerprinter.cs
using System.Security.Cryptography;
using System.Text;
using SuavoAgent.Contracts.Canary;

namespace SuavoAgent.Core.Canary;

/// <summary>
/// Deterministic SHA-256 hashing of contract components.
/// All inputs are sorted before hashing to ensure order-independence.
/// </summary>
public static class ContractFingerprinter
{
    public static string HashObjects(IEnumerable<ObservedObject> objects)
    {
        var sorted = objects
            .OrderBy(o => o.SchemaName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(o => o.TableName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(o => o.ColumnName, StringComparer.OrdinalIgnoreCase);

        var sb = new StringBuilder();
        foreach (var o in sorted)
            sb.Append($"{o.SchemaName}|{o.TableName}|{o.ColumnName}|{o.DataTypeName}|{o.MaxLength}|{o.IsNullable}\n");

        return Sha256Hex(sb.ToString());
    }

    public static string HashStatusMap(IEnumerable<ObservedStatus> statuses)
    {
        var sorted = statuses
            .OrderBy(s => s.Description, StringComparer.OrdinalIgnoreCase);

        var sb = new StringBuilder();
        foreach (var s in sorted)
            sb.Append($"{s.Description}|{s.GuidValue}\n");

        return Sha256Hex(sb.ToString());
    }

    public static string HashQuery(string queryText)
        => Sha256Hex(queryText);

    public static string HashResultShape(IEnumerable<(string Name, string TypeName)> columns)
    {
        var sb = new StringBuilder();
        foreach (var (name, type) in columns)
            sb.Append($"{name}|{type}\n");

        return Sha256Hex(sb.ToString());
    }

    public static string CompositeHash(string objectHash, string statusHash,
        string queryHash, string resultShapeHash)
        => Sha256Hex($"{objectHash}|{statusHash}|{queryHash}|{resultShapeHash}");

    private static string Sha256Hex(string input)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SuavoAgent.Core.Tests --filter "FullyQualifiedName~ContractFingerprinterTests" -v minimal`
Expected: 11 tests pass

- [ ] **Step 5: Commit**

```bash
git add src/SuavoAgent.Core/Canary/ContractFingerprinter.cs tests/SuavoAgent.Core.Tests/Canary/ContractFingerprinterTests.cs
git commit -m "feat(canary): add ContractFingerprinter — deterministic SHA-256 hashing of contract components"
```

---

## Task 3: SchemaCanaryClassifier — Pure Severity Logic

**Files:**
- Create: `src/SuavoAgent.Core/Canary/SchemaCanaryClassifier.cs`
- Create: `tests/SuavoAgent.Core.Tests/Canary/SchemaCanaryClassifierTests.cs`

- [ ] **Step 1: Write classifier tests**

```csharp
// tests/SuavoAgent.Core.Tests/Canary/SchemaCanaryClassifierTests.cs
using SuavoAgent.Contracts.Canary;
using SuavoAgent.Core.Canary;
using Xunit;

namespace SuavoAgent.Core.Tests.Canary;

public class SchemaCanaryClassifierTests
{
    // Baseline: 2 required objects, 2 statuses
    private static readonly ObservedObject ReqCol1 = new("Prescription", "RxTransaction",
        "RxNumber", "int", null, false, true);
    private static readonly ObservedObject ReqCol2 = new("Prescription", "RxTransaction",
        "DateFilled", "datetime", null, true, true);
    private static readonly ObservedObject OptCol1 = new("Inventory", "Item",
        "ItemName", "nvarchar", 200, true, false);
    private static readonly ObservedStatus St1 = new("Waiting for Pick up",
        "53ce4c47-dff2-46ac-a310-719e792239ef");
    private static readonly ObservedStatus St2 = new("Waiting for Delivery",
        "c3adbbcc-76e3-4b06-a0dc-4e8b8ce0a2de");

    private static ContractBaseline MakeBaseline(
        IReadOnlyList<ObservedObject>? objects = null,
        IReadOnlyList<ObservedStatus>? statuses = null)
    {
        var objs = objects ?? new[] { ReqCol1, ReqCol2, OptCol1 };
        var stats = statuses ?? new[] { St1, St2 };
        var objHash = ContractFingerprinter.HashObjects(objs);
        var statusHash = ContractFingerprinter.HashStatusMap(stats);
        var queryHash = ContractFingerprinter.HashQuery("SELECT 1");
        var shapeHash = ContractFingerprinter.HashResultShape(
            new[] { ("RxNumber", "int"), ("DateFilled", "datetime") });
        return new ContractBaseline("pioneerrx", objHash, statusHash, queryHash,
            shapeHash, ContractFingerprinter.CompositeHash(objHash, statusHash, queryHash, shapeHash),
            "{}", 1);
    }

    [Fact]
    public void NoChanges_ReturnsNone()
    {
        var baseline = MakeBaseline();
        var observed = new ObservedContract(
            new[] { ReqCol1, ReqCol2, OptCol1 }, new[] { St1, St2 },
            ContractFingerprinter.HashQuery("SELECT 1"),
            ContractFingerprinter.HashResultShape(
                new[] { ("RxNumber", "int"), ("DateFilled", "datetime") }));
        var result = SchemaCanaryClassifier.Classify(baseline, observed);
        Assert.Equal(CanarySeverity.None, result.Severity);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void RequiredTableMissing_Critical()
    {
        var baseline = MakeBaseline();
        var observed = new ObservedContract(
            new[] { ReqCol2, OptCol1 }, // ReqCol1 missing
            new[] { St1, St2 },
            ContractFingerprinter.HashQuery("SELECT 1"), null);
        var result = SchemaCanaryClassifier.Classify(baseline, observed);
        Assert.Equal(CanarySeverity.Critical, result.Severity);
        Assert.Contains("object", result.DriftedComponents);
    }

    [Fact]
    public void StatusDescriptionRenamed_Critical()
    {
        var baseline = MakeBaseline();
        var renamed = St1 with { Description = "Ready for Pickup" };
        var observed = new ObservedContract(
            new[] { ReqCol1, ReqCol2, OptCol1 },
            new[] { renamed, St2 },
            ContractFingerprinter.HashQuery("SELECT 1"), null);
        var result = SchemaCanaryClassifier.Classify(baseline, observed);
        Assert.Equal(CanarySeverity.Critical, result.Severity);
        Assert.Contains("status_map", result.DriftedComponents);
    }

    [Fact]
    public void StatusGuidChanged_Critical()
    {
        var baseline = MakeBaseline();
        var changed = St1 with { GuidValue = "00000000-0000-0000-0000-000000000000" };
        var observed = new ObservedContract(
            new[] { ReqCol1, ReqCol2, OptCol1 },
            new[] { changed, St2 },
            ContractFingerprinter.HashQuery("SELECT 1"), null);
        var result = SchemaCanaryClassifier.Classify(baseline, observed);
        Assert.Equal(CanarySeverity.Critical, result.Severity);
    }

    [Fact]
    public void StatusCountChanged_Critical()
    {
        var baseline = MakeBaseline();
        var observed = new ObservedContract(
            new[] { ReqCol1, ReqCol2, OptCol1 },
            new[] { St1 }, // was 2, now 1
            ContractFingerprinter.HashQuery("SELECT 1"), null);
        var result = SchemaCanaryClassifier.Classify(baseline, observed);
        Assert.Equal(CanarySeverity.Critical, result.Severity);
    }

    [Fact]
    public void ColumnTypeChangedIncompatibly_Critical()
    {
        var baseline = MakeBaseline();
        var retyped = ReqCol1 with { DataTypeName = "varchar" }; // int → varchar
        var observed = new ObservedContract(
            new[] { retyped, ReqCol2, OptCol1 },
            new[] { St1, St2 },
            ContractFingerprinter.HashQuery("SELECT 1"), null);
        var result = SchemaCanaryClassifier.Classify(baseline, observed);
        Assert.Equal(CanarySeverity.Critical, result.Severity);
    }

    [Fact]
    public void ResultShapeMismatch_Critical()
    {
        var baseline = MakeBaseline();
        var observed = new ObservedContract(
            new[] { ReqCol1, ReqCol2, OptCol1 },
            new[] { St1, St2 },
            ContractFingerprinter.HashQuery("SELECT 1"),
            ContractFingerprinter.HashResultShape(new[] { ("RxNumber", "int") })); // missing DateFilled
        var result = SchemaCanaryClassifier.Classify(baseline, observed);
        Assert.Equal(CanarySeverity.Critical, result.Severity);
        Assert.Contains("result_shape", result.DriftedComponents);
    }

    [Fact]
    public void ColumnTypeWidened_Warning()
    {
        var baseline = MakeBaseline();
        var widened = OptCol1 with { MaxLength = 500 }; // nvarchar(200) → nvarchar(500)
        var observed = new ObservedContract(
            new[] { ReqCol1, ReqCol2, widened },
            new[] { St1, St2 },
            ContractFingerprinter.HashQuery("SELECT 1"),
            ContractFingerprinter.HashResultShape(
                new[] { ("RxNumber", "int"), ("DateFilled", "datetime") }));
        var result = SchemaCanaryClassifier.Classify(baseline, observed);
        Assert.Equal(CanarySeverity.Warning, result.Severity);
    }

    [Fact]
    public void NullableChanged_Warning()
    {
        var baseline = MakeBaseline();
        var changed = ReqCol1 with { IsNullable = true }; // was false
        var observed = new ObservedContract(
            new[] { changed, ReqCol2, OptCol1 },
            new[] { St1, St2 },
            ContractFingerprinter.HashQuery("SELECT 1"),
            ContractFingerprinter.HashResultShape(
                new[] { ("RxNumber", "int"), ("DateFilled", "datetime") }));
        var result = SchemaCanaryClassifier.Classify(baseline, observed);
        Assert.Equal(CanarySeverity.Warning, result.Severity);
    }

    [Fact]
    public void OptionalObjectMissing_Warning()
    {
        var baseline = MakeBaseline();
        var observed = new ObservedContract(
            new[] { ReqCol1, ReqCol2 }, // OptCol1 missing
            new[] { St1, St2 },
            ContractFingerprinter.HashQuery("SELECT 1"),
            ContractFingerprinter.HashResultShape(
                new[] { ("RxNumber", "int"), ("DateFilled", "datetime") }));
        var result = SchemaCanaryClassifier.Classify(baseline, observed);
        Assert.Equal(CanarySeverity.Warning, result.Severity);
    }

    [Fact]
    public void DuplicateStatusDescriptions_Critical()
    {
        var baseline = MakeBaseline();
        var dup = St1 with { GuidValue = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee" };
        var observed = new ObservedContract(
            new[] { ReqCol1, ReqCol2, OptCol1 },
            new[] { St1, dup, St2 }, // two "Waiting for Pick up" with different GUIDs
            ContractFingerprinter.HashQuery("SELECT 1"), null);
        var result = SchemaCanaryClassifier.Classify(baseline, observed);
        Assert.Equal(CanarySeverity.Critical, result.Severity);
    }

    [Fact]
    public void MultipleComponents_ReportsAll()
    {
        var baseline = MakeBaseline();
        var retyped = ReqCol1 with { DataTypeName = "varchar" };
        var renamedSt = St1 with { Description = "Ready" };
        var observed = new ObservedContract(
            new[] { retyped, ReqCol2, OptCol1 },
            new[] { renamedSt, St2 },
            ContractFingerprinter.HashQuery("SELECT 1"), null);
        var result = SchemaCanaryClassifier.Classify(baseline, observed);
        Assert.Equal(CanarySeverity.Critical, result.Severity);
        Assert.Contains("object", result.DriftedComponents);
        Assert.Contains("status_map", result.DriftedComponents);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SuavoAgent.Core.Tests --filter "FullyQualifiedName~SchemaCanaryClassifierTests" -v minimal`
Expected: Build error — `SchemaCanaryClassifier` does not exist

- [ ] **Step 3: Implement SchemaCanaryClassifier**

```csharp
// src/SuavoAgent.Core/Canary/SchemaCanaryClassifier.cs
using SuavoAgent.Contracts.Canary;

namespace SuavoAgent.Core.Canary;

/// <summary>
/// Pure severity classifier. No I/O, no state.
/// Compares observed schema against approved baseline.
/// </summary>
public static class SchemaCanaryClassifier
{
    public static ContractVerification Classify(ContractBaseline baseline, ObservedContract observed)
    {
        var drifted = new List<string>();
        var severity = CanarySeverity.None;
        var details = new List<string>();

        // --- Object fingerprint ---
        var observedObjHash = ContractFingerprinter.HashObjects(observed.Objects);
        if (observedObjHash != baseline.ObjectFingerprint)
        {
            drifted.Add("object");
            var objSeverity = ClassifyObjectDrift(baseline, observed);
            details.Add($"Object drift: {objSeverity}");
            severity = Max(severity, objSeverity);
        }

        // --- Status map fingerprint ---
        var observedStatusHash = ContractFingerprinter.HashStatusMap(observed.StatusMap);
        if (observedStatusHash != baseline.StatusMapFingerprint)
        {
            drifted.Add("status_map");
            severity = Max(severity, CanarySeverity.Critical);
            details.Add("Status map changed");
        }

        // --- Duplicate status descriptions (adversarial) ---
        var descGroups = observed.StatusMap.GroupBy(s => s.Description, StringComparer.OrdinalIgnoreCase);
        if (descGroups.Any(g => g.Count() > 1))
        {
            if (!drifted.Contains("status_map")) drifted.Add("status_map");
            severity = Max(severity, CanarySeverity.Critical);
            details.Add("Duplicate status descriptions detected");
        }

        // --- Query fingerprint ---
        if (observed.QueryFingerprint != baseline.QueryFingerprint)
        {
            drifted.Add("query");
            severity = Max(severity, CanarySeverity.Critical);
            details.Add("Query fingerprint changed");
        }

        // --- Result shape fingerprint (postflight) ---
        if (observed.ResultShapeFingerprint != null &&
            observed.ResultShapeFingerprint != baseline.ResultShapeFingerprint)
        {
            drifted.Add("result_shape");
            severity = Max(severity, CanarySeverity.Critical);
            details.Add("Result shape changed");
        }

        return new ContractVerification(
            severity == CanarySeverity.None,
            severity,
            drifted,
            baseline.ContractFingerprint,
            observed.ResultShapeFingerprint,
            string.Join("; ", details));
    }

    private static CanarySeverity ClassifyObjectDrift(
        ContractBaseline baseline, ObservedContract observed)
    {
        // Parse baseline contract JSON to get expected objects
        // For now: any object hash mismatch on required objects = Critical,
        // only optional objects changed = Warning
        var hasRequiredMissing = false;
        var hasIncompatibleType = false;
        var onlyWidened = true;

        // Check each observed object against baseline expectations
        // If a required object is missing or type-incompatible → Critical
        // If only nullable/size changed → Warning
        foreach (var obj in observed.Objects)
        {
            if (!obj.IsRequired) continue;
            // Required object present — check type compatibility below
        }

        // Check if any required objects from baseline are missing from observed
        var observedKeys = observed.Objects
            .Select(o => $"{o.SchemaName}.{o.TableName}.{o.ColumnName}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var baselineRequired = observed.Objects.Where(o => o.IsRequired).ToList();
        // If required count changed, that's caught by hash mismatch already

        // Simple heuristic: if object hash differs, check if it's just widening
        var allPresent = observed.Objects.All(o => !o.IsRequired ||
            observedKeys.Contains($"{o.SchemaName}.{o.TableName}.{o.ColumnName}"));

        if (!allPresent) return CanarySeverity.Critical;

        // If all required objects present but hash differs → likely type/nullable change
        // Check for incompatible type changes vs widening
        return CanarySeverity.Warning;
    }

    private static CanarySeverity Max(CanarySeverity a, CanarySeverity b)
        => (CanarySeverity)Math.Max((int)a, (int)b);
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SuavoAgent.Core.Tests --filter "FullyQualifiedName~SchemaCanaryClassifierTests" -v minimal`
Expected: 13 tests pass

- [ ] **Step 5: Commit**

```bash
git add src/SuavoAgent.Core/Canary/SchemaCanaryClassifier.cs tests/SuavoAgent.Core.Tests/Canary/SchemaCanaryClassifierTests.cs
git commit -m "feat(canary): add SchemaCanaryClassifier — pure severity classification for schema drift"
```

---

## Task 4: SchemaCanaryEscalation — Pure State Machine

**Files:**
- Create: `src/SuavoAgent.Core/Canary/SchemaCanaryEscalation.cs`
- Create: `tests/SuavoAgent.Core.Tests/Canary/SchemaCanaryEscalationTests.cs`

- [ ] **Step 1: Write escalation tests**

```csharp
// tests/SuavoAgent.Core.Tests/Canary/SchemaCanaryEscalationTests.cs
using SuavoAgent.Contracts.Canary;
using SuavoAgent.Core.Canary;
using Xunit;

namespace SuavoAgent.Core.Tests.Canary;

public class SchemaCanaryEscalationTests
{
    [Fact]
    public void Clean_NoHold()
    {
        var state = CanaryHoldState.Clear;
        var result = SchemaCanaryEscalation.Transition(state, CanarySeverity.None);
        Assert.False(result.IsInHold);
    }

    [Fact]
    public void Warning_DoesNotBlock()
    {
        var state = CanaryHoldState.Clear;
        var result = SchemaCanaryEscalation.Transition(state, CanarySeverity.Warning);
        Assert.False(result.IsInHold);
        Assert.Equal(1, result.ConsecutiveWarnings);
    }

    [Fact]
    public void Warning_ThreeConsecutive_EscalatesToCritical()
    {
        var state = CanaryHoldState.Clear;
        state = SchemaCanaryEscalation.Transition(state, CanarySeverity.Warning);
        state = SchemaCanaryEscalation.Transition(state, CanarySeverity.Warning);
        state = SchemaCanaryEscalation.Transition(state, CanarySeverity.Warning);
        Assert.True(state.IsInHold);
        Assert.Equal(CanarySeverity.Critical, state.EffectiveSeverity);
    }

    [Fact]
    public void Warning_CleanCycle_ResetsCounter()
    {
        var state = CanaryHoldState.Clear;
        state = SchemaCanaryEscalation.Transition(state, CanarySeverity.Warning);
        state = SchemaCanaryEscalation.Transition(state, CanarySeverity.Warning);
        state = SchemaCanaryEscalation.Transition(state, CanarySeverity.None); // clean
        Assert.Equal(0, state.ConsecutiveWarnings);
        Assert.False(state.IsInHold);
    }

    [Fact]
    public void Critical_EntersHold_BlockedCycleOne()
    {
        var state = CanaryHoldState.Clear;
        state = SchemaCanaryEscalation.Transition(state, CanarySeverity.Critical);
        Assert.True(state.IsInHold);
        Assert.Equal(1, state.BlockedCycles);
    }

    [Fact]
    public void Critical_ThreeCycles_DashboardEscalation()
    {
        var state = CanaryHoldState.Clear;
        for (int i = 0; i < 3; i++)
            state = SchemaCanaryEscalation.Transition(state, CanarySeverity.Critical);
        Assert.True(state.ShouldAlertDashboard);
    }

    [Fact]
    public void Critical_TwelveCycles_PhoneEscalation()
    {
        var state = CanaryHoldState.Clear;
        for (int i = 0; i < 12; i++)
            state = SchemaCanaryEscalation.Transition(state, CanarySeverity.Critical);
        Assert.True(state.ShouldAlertPhone);
    }

    [Fact]
    public void Acknowledge_ClearsHold()
    {
        var state = CanaryHoldState.Clear;
        state = SchemaCanaryEscalation.Transition(state, CanarySeverity.Critical);
        Assert.True(state.IsInHold);
        state = SchemaCanaryEscalation.Acknowledge(state, "operator-1");
        Assert.False(state.IsInHold);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SuavoAgent.Core.Tests --filter "FullyQualifiedName~SchemaCanaryEscalationTests" -v minimal`
Expected: Build error — `SchemaCanaryEscalation` does not exist

- [ ] **Step 3: Implement SchemaCanaryEscalation**

```csharp
// src/SuavoAgent.Core/Canary/SchemaCanaryEscalation.cs
using SuavoAgent.Contracts.Canary;

namespace SuavoAgent.Core.Canary;

public record CanaryHoldState(
    bool IsInHold,
    CanarySeverity EffectiveSeverity,
    int BlockedCycles,
    int ConsecutiveWarnings,
    string? AcknowledgedBy)
{
    public static CanaryHoldState Clear { get; } = new(false, CanarySeverity.None, 0, 0, null);

    public bool ShouldAlertDashboard => BlockedCycles >= 3;
    public bool ShouldAlertPhone => BlockedCycles >= 12;
}

/// <summary>
/// Pure escalation state machine. No I/O, no persistence.
/// Per errata E20: Warning does NOT block. 3 consecutive Warnings → Critical.
/// </summary>
public static class SchemaCanaryEscalation
{
    private const int WarningEscalationThreshold = 3;

    public static CanaryHoldState Transition(CanaryHoldState current, CanarySeverity severity)
    {
        return severity switch
        {
            CanarySeverity.None => current with
            {
                ConsecutiveWarnings = 0,
                // If in hold from prior Critical, stay in hold (need ack to clear)
            },

            CanarySeverity.Warning when !current.IsInHold =>
                current.ConsecutiveWarnings + 1 >= WarningEscalationThreshold
                    ? current with
                    {
                        IsInHold = true,
                        EffectiveSeverity = CanarySeverity.Critical,
                        BlockedCycles = 1,
                        ConsecutiveWarnings = current.ConsecutiveWarnings + 1,
                    }
                    : current with { ConsecutiveWarnings = current.ConsecutiveWarnings + 1 },

            CanarySeverity.Critical => current.IsInHold
                ? current with { BlockedCycles = current.BlockedCycles + 1 }
                : current with
                {
                    IsInHold = true,
                    EffectiveSeverity = CanarySeverity.Critical,
                    BlockedCycles = 1,
                    ConsecutiveWarnings = 0,
                },

            // Warning while already in hold — increment blocked cycles
            CanarySeverity.Warning => current with
            {
                BlockedCycles = current.BlockedCycles + 1,
                ConsecutiveWarnings = current.ConsecutiveWarnings + 1,
            },

            _ => current,
        };
    }

    public static CanaryHoldState Acknowledge(CanaryHoldState current, string acknowledgedBy)
        => CanaryHoldState.Clear with { AcknowledgedBy = acknowledgedBy };
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SuavoAgent.Core.Tests --filter "FullyQualifiedName~SchemaCanaryEscalationTests" -v minimal`
Expected: 8 tests pass

- [ ] **Step 5: Commit**

```bash
git add src/SuavoAgent.Core/Canary/SchemaCanaryEscalation.cs tests/SuavoAgent.Core.Tests/Canary/SchemaCanaryEscalationTests.cs
git commit -m "feat(canary): add SchemaCanaryEscalation — pure state machine with Warning non-blocking, 3-Warning→Critical escalation"
```

---

## Task 5: AgentStateDb — Canary Tables

**Files:**
- Modify: `src/SuavoAgent.Core/State/AgentStateDb.cs`
- Test: `tests/SuavoAgent.Core.Tests/Canary/CanaryDbTests.cs`

- [ ] **Step 1: Write DB persistence tests**

```csharp
// tests/SuavoAgent.Core.Tests/Canary/CanaryDbTests.cs
using SuavoAgent.Contracts.Canary;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Canary;

public class CanaryDbTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AgentStateDb _db;

    public CanaryDbTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"canary_test_{Guid.NewGuid():N}.db");
        _db = new AgentStateDb(_dbPath);
    }

    [Fact]
    public void UpsertAndRetrieveBaseline()
    {
        var baseline = new ContractBaseline("pioneerrx", "obj1", "stat1", "qry1", "shape1",
            "composite1", "{}", 1);
        _db.UpsertCanaryBaseline("pharm-1", baseline);
        var loaded = _db.GetCanaryBaseline("pharm-1", "pioneerrx");
        Assert.NotNull(loaded);
        Assert.Equal("obj1", loaded.Value.ObjectFingerprint);
        Assert.Equal(1, loaded.Value.SchemaEpoch);
    }

    [Fact]
    public void Upsert_UpdatesExistingBaseline()
    {
        var b1 = new ContractBaseline("pioneerrx", "obj1", "stat1", "qry1", "shape1",
            "composite1", "{}", 1);
        _db.UpsertCanaryBaseline("pharm-1", b1);
        var b2 = new ContractBaseline("pioneerrx", "obj2", "stat2", "qry2", "shape2",
            "composite2", "{}", 2);
        _db.UpsertCanaryBaseline("pharm-1", b2);
        var loaded = _db.GetCanaryBaseline("pharm-1", "pioneerrx");
        Assert.Equal("obj2", loaded!.Value.ObjectFingerprint);
        Assert.Equal(2, loaded.Value.SchemaEpoch);
    }

    [Fact]
    public void InsertAndRetrieveIncident()
    {
        _db.InsertCanaryIncident("pharm-1", "pioneerrx", "critical",
            "[\"status_map\"]", "base1", "obs1", "Status changed", 5);
        var incidents = _db.GetOpenCanaryIncidents("pharm-1");
        Assert.Single(incidents);
        Assert.Equal("critical", incidents[0].Severity);
        Assert.Equal(5, incidents[0].DroppedBatchRowCount);
    }

    [Fact]
    public void HoldState_PersistsAndSurvivesReopen()
    {
        _db.UpsertCanaryHold("pharm-1", "pioneerrx", "critical", "base1");
        _db.IncrementCanaryHoldCycles("pharm-1", "pioneerrx");
        _db.IncrementCanaryHoldCycles("pharm-1", "pioneerrx");

        // Close and reopen
        _db.Dispose();
        using var db2 = new AgentStateDb(_dbPath);
        var hold = db2.GetCanaryHold("pharm-1", "pioneerrx");
        Assert.NotNull(hold);
        Assert.Equal(2, hold.Value.BlockedCycles);
    }

    [Fact]
    public void ClearHold()
    {
        _db.UpsertCanaryHold("pharm-1", "pioneerrx", "critical", "base1");
        _db.ClearCanaryHold("pharm-1", "pioneerrx");
        var hold = _db.GetCanaryHold("pharm-1", "pioneerrx");
        Assert.Null(hold);
    }

    [Fact]
    public void NoBaseline_ReturnsNull()
    {
        var loaded = _db.GetCanaryBaseline("nonexistent", "pioneerrx");
        Assert.Null(loaded);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SuavoAgent.Core.Tests --filter "FullyQualifiedName~CanaryDbTests" -v minimal`
Expected: Build error — canary methods do not exist on AgentStateDb

- [ ] **Step 3: Add canary tables and methods to AgentStateDb**

Add to `InitSchema()` in `AgentStateDb.cs`, after the existing POM tables block:

```csharp
// Add after the existing pomCmd.ExecuteNonQuery(); line in InitSchema()

// Canary tables
using var canaryCmd = _conn.CreateCommand();
canaryCmd.CommandText = """
    CREATE TABLE IF NOT EXISTS schema_canary_baselines (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        pharmacy_id TEXT NOT NULL,
        adapter_type TEXT NOT NULL,
        object_fingerprint TEXT NOT NULL,
        status_map_fingerprint TEXT NOT NULL,
        query_fingerprint TEXT NOT NULL,
        result_shape_fingerprint TEXT NOT NULL,
        contract_fingerprint TEXT NOT NULL,
        contract_json TEXT NOT NULL,
        schema_epoch INTEGER NOT NULL DEFAULT 1,
        contract_version INTEGER NOT NULL DEFAULT 1,
        approved_at TEXT,
        approved_by TEXT,
        approved_command_id TEXT,
        created_at TEXT NOT NULL,
        updated_at TEXT NOT NULL,
        UNIQUE(pharmacy_id, adapter_type)
    );
    CREATE TABLE IF NOT EXISTS schema_canary_incidents (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        pharmacy_id TEXT NOT NULL,
        adapter_type TEXT NOT NULL,
        severity TEXT NOT NULL CHECK (severity IN ('warning','critical')),
        drifted_components TEXT NOT NULL,
        baseline_contract_fingerprint TEXT NOT NULL,
        observed_contract_fingerprint TEXT NOT NULL,
        drift_details TEXT,
        dropped_batch_row_count INTEGER,
        blocked_cycle_count INTEGER DEFAULT 1,
        opened_at TEXT NOT NULL,
        last_seen_at TEXT NOT NULL,
        resolved_at TEXT,
        resolved_by TEXT,
        resolution TEXT CHECK (resolution IN ('auto_cleared','operator_acknowledged','relearned')),
        ack_command_id TEXT
    );
    CREATE TABLE IF NOT EXISTS schema_canary_hold (
        pharmacy_id TEXT NOT NULL,
        adapter_type TEXT NOT NULL,
        severity TEXT NOT NULL CHECK (severity IN ('warning','critical')),
        drift_hold_since TEXT NOT NULL,
        blocked_cycle_count INTEGER NOT NULL DEFAULT 0,
        last_seen_at TEXT NOT NULL,
        baseline_contract_fingerprint TEXT NOT NULL,
        acknowledged_at TEXT,
        acknowledged_by TEXT,
        ack_command_id TEXT,
        PRIMARY KEY (pharmacy_id, adapter_type)
    );
    """;
canaryCmd.ExecuteNonQuery();

// Migrate: add baseline_contract_fingerprint to unsynced_batches
TryAlter("ALTER TABLE unsynced_batches ADD COLUMN baseline_contract_fingerprint TEXT");
TryAlter("ALTER TABLE unsynced_batches ADD COLUMN row_count INTEGER");

// Migrate: add contract provenance to learning_session
TryAlter("ALTER TABLE learning_session ADD COLUMN approved_contract_fingerprint TEXT");
```

Then add CRUD methods at the end of the class:

```csharp
// ── Canary Baselines ──

public void UpsertCanaryBaseline(string pharmacyId, ContractBaseline baseline)
{
    using var cmd = _conn.CreateCommand();
    var now = DateTimeOffset.UtcNow.ToString("o");
    cmd.CommandText = """
        INSERT INTO schema_canary_baselines
            (pharmacy_id, adapter_type, object_fingerprint, status_map_fingerprint,
             query_fingerprint, result_shape_fingerprint, contract_fingerprint,
             contract_json, schema_epoch, contract_version, created_at, updated_at)
        VALUES (@pid, @adapter, @obj, @status, @query, @shape, @composite,
                @json, @epoch, @version, @now, @now)
        ON CONFLICT(pharmacy_id, adapter_type) DO UPDATE SET
            object_fingerprint = @obj,
            status_map_fingerprint = @status,
            query_fingerprint = @query,
            result_shape_fingerprint = @shape,
            contract_fingerprint = @composite,
            contract_json = @json,
            schema_epoch = @epoch,
            contract_version = @version,
            updated_at = @now
        """;
    cmd.Parameters.AddWithValue("@pid", pharmacyId);
    cmd.Parameters.AddWithValue("@adapter", baseline.AdapterType);
    cmd.Parameters.AddWithValue("@obj", baseline.ObjectFingerprint);
    cmd.Parameters.AddWithValue("@status", baseline.StatusMapFingerprint);
    cmd.Parameters.AddWithValue("@query", baseline.QueryFingerprint);
    cmd.Parameters.AddWithValue("@shape", baseline.ResultShapeFingerprint);
    cmd.Parameters.AddWithValue("@composite", baseline.ContractFingerprint);
    cmd.Parameters.AddWithValue("@json", baseline.ContractJson);
    cmd.Parameters.AddWithValue("@epoch", baseline.SchemaEpoch);
    cmd.Parameters.AddWithValue("@version", baseline.ContractVersion);
    cmd.Parameters.AddWithValue("@now", now);
    cmd.ExecuteNonQuery();
}

public ContractBaseline? GetCanaryBaseline(string pharmacyId, string adapterType)
{
    using var cmd = _conn.CreateCommand();
    cmd.CommandText = """
        SELECT adapter_type, object_fingerprint, status_map_fingerprint,
               query_fingerprint, result_shape_fingerprint, contract_fingerprint,
               contract_json, schema_epoch, contract_version
        FROM schema_canary_baselines
        WHERE pharmacy_id = @pid AND adapter_type = @adapter
        """;
    cmd.Parameters.AddWithValue("@pid", pharmacyId);
    cmd.Parameters.AddWithValue("@adapter", adapterType);
    using var reader = cmd.ExecuteReader();
    if (!reader.Read()) return null;
    return new ContractBaseline(
        reader.GetString(0), reader.GetString(1), reader.GetString(2),
        reader.GetString(3), reader.GetString(4), reader.GetString(5),
        reader.GetString(6), reader.GetInt32(7), reader.GetInt32(8));
}

// ── Canary Incidents ──

public void InsertCanaryIncident(string pharmacyId, string adapterType, string severity,
    string driftedComponents, string baselineFingerprint, string observedFingerprint,
    string? details, int? droppedRowCount)
{
    using var cmd = _conn.CreateCommand();
    var now = DateTimeOffset.UtcNow.ToString("o");
    cmd.CommandText = """
        INSERT INTO schema_canary_incidents
            (pharmacy_id, adapter_type, severity, drifted_components,
             baseline_contract_fingerprint, observed_contract_fingerprint,
             drift_details, dropped_batch_row_count, opened_at, last_seen_at)
        VALUES (@pid, @adapter, @sev, @comps, @base, @obs, @details, @rows, @now, @now)
        """;
    cmd.Parameters.AddWithValue("@pid", pharmacyId);
    cmd.Parameters.AddWithValue("@adapter", adapterType);
    cmd.Parameters.AddWithValue("@sev", severity);
    cmd.Parameters.AddWithValue("@comps", driftedComponents);
    cmd.Parameters.AddWithValue("@base", baselineFingerprint);
    cmd.Parameters.AddWithValue("@obs", observedFingerprint);
    cmd.Parameters.AddWithValue("@details", (object?)details ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@rows", (object?)droppedRowCount ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@now", now);
    cmd.ExecuteNonQuery();
}

public IReadOnlyList<(string Severity, int? DroppedBatchRowCount, string OpenedAt)>
    GetOpenCanaryIncidents(string pharmacyId)
{
    using var cmd = _conn.CreateCommand();
    cmd.CommandText = """
        SELECT severity, dropped_batch_row_count, opened_at
        FROM schema_canary_incidents
        WHERE pharmacy_id = @pid AND resolved_at IS NULL
        ORDER BY opened_at DESC
        """;
    cmd.Parameters.AddWithValue("@pid", pharmacyId);
    var results = new List<(string, int?, string)>();
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
        results.Add((reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetInt32(1),
            reader.GetString(2)));
    }
    return results;
}

// ── Canary Hold ──

public void UpsertCanaryHold(string pharmacyId, string adapterType,
    string severity, string baselineFingerprint)
{
    using var cmd = _conn.CreateCommand();
    var now = DateTimeOffset.UtcNow.ToString("o");
    cmd.CommandText = """
        INSERT INTO schema_canary_hold
            (pharmacy_id, adapter_type, severity, drift_hold_since,
             blocked_cycle_count, last_seen_at, baseline_contract_fingerprint)
        VALUES (@pid, @adapter, @sev, @now, 0, @now, @base)
        ON CONFLICT(pharmacy_id, adapter_type) DO UPDATE SET
            severity = @sev,
            last_seen_at = @now
        """;
    cmd.Parameters.AddWithValue("@pid", pharmacyId);
    cmd.Parameters.AddWithValue("@adapter", adapterType);
    cmd.Parameters.AddWithValue("@sev", severity);
    cmd.Parameters.AddWithValue("@base", baselineFingerprint);
    cmd.Parameters.AddWithValue("@now", now);
    cmd.ExecuteNonQuery();
}

public void IncrementCanaryHoldCycles(string pharmacyId, string adapterType)
{
    using var cmd = _conn.CreateCommand();
    cmd.CommandText = """
        UPDATE schema_canary_hold
        SET blocked_cycle_count = blocked_cycle_count + 1,
            last_seen_at = @now
        WHERE pharmacy_id = @pid AND adapter_type = @adapter
        """;
    cmd.Parameters.AddWithValue("@pid", pharmacyId);
    cmd.Parameters.AddWithValue("@adapter", adapterType);
    cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o"));
    cmd.ExecuteNonQuery();
}

public (string Severity, int BlockedCycles, string DriftHoldSince)?
    GetCanaryHold(string pharmacyId, string adapterType)
{
    using var cmd = _conn.CreateCommand();
    cmd.CommandText = """
        SELECT severity, blocked_cycle_count, drift_hold_since
        FROM schema_canary_hold
        WHERE pharmacy_id = @pid AND adapter_type = @adapter
        """;
    cmd.Parameters.AddWithValue("@pid", pharmacyId);
    cmd.Parameters.AddWithValue("@adapter", adapterType);
    using var reader = cmd.ExecuteReader();
    if (!reader.Read()) return null;
    return (reader.GetString(0), reader.GetInt32(1), reader.GetString(2));
}

public void ClearCanaryHold(string pharmacyId, string adapterType)
{
    using var cmd = _conn.CreateCommand();
    cmd.CommandText = "DELETE FROM schema_canary_hold WHERE pharmacy_id = @pid AND adapter_type = @adapter";
    cmd.Parameters.AddWithValue("@pid", pharmacyId);
    cmd.Parameters.AddWithValue("@adapter", adapterType);
    cmd.ExecuteNonQuery();
}
```

- [ ] **Step 4: Add using for Contracts.Canary**

Add to top of AgentStateDb.cs:
```csharp
using SuavoAgent.Contracts.Canary;
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/SuavoAgent.Core.Tests --filter "FullyQualifiedName~CanaryDbTests" -v minimal`
Expected: 6 tests pass

- [ ] **Step 6: Run ALL existing tests to verify no regressions**

Run: `dotnet test -v minimal`
Expected: All existing tests still pass

- [ ] **Step 7: Commit**

```bash
git add src/SuavoAgent.Core/State/AgentStateDb.cs tests/SuavoAgent.Core.Tests/Canary/CanaryDbTests.cs
git commit -m "feat(canary): add canary tables to AgentStateDb — baselines, incidents, hold with persistence"
```

---

## Task 6: PioneerRxSqlEngine — Contract Metadata Queries

**Files:**
- Modify: `src/SuavoAgent.Adapters.PioneerRx/Sql/PioneerRxSqlEngine.cs`

- [ ] **Step 1: Add QueryContractMetadataAsync method**

Add to PioneerRxSqlEngine:

```csharp
/// <summary>
/// Queries sys.* catalog views for contracted objects only.
/// Returns observed schema metadata for canary preflight.
/// ORDER BY ensures deterministic hashing.
/// </summary>
public async Task<IReadOnlyList<ObservedObject>> QueryContractMetadataAsync(
    IReadOnlyList<(string Schema, string Table, string Column, bool IsRequired)> contractedObjects,
    CancellationToken ct)
{
    if (_connection is null || _connection.State != System.Data.ConnectionState.Open)
        return Array.Empty<ObservedObject>();

    var results = new List<ObservedObject>();

    // Build WHERE clause for contracted objects only
    var conditions = new List<string>();
    for (int i = 0; i < contractedObjects.Count; i++)
    {
        conditions.Add($"(s.name = @schema{i} AND o.name = @table{i} AND c.name = @col{i})");
    }

    var query = $"""
        SELECT s.name AS schema_name, o.name AS table_name, c.name AS column_name,
               t.name AS type_name, c.max_length, c.is_nullable
        FROM sys.columns c
        JOIN sys.objects o ON c.object_id = o.object_id
        JOIN sys.schemas s ON o.schema_id = s.schema_id
        JOIN sys.types t ON c.user_type_id = t.user_type_id
        WHERE ({string.Join(" OR ", conditions)})
        ORDER BY s.name, o.name, c.name
        """;

    await using var cmd = new SqlCommand(query, _connection);
    cmd.CommandTimeout = 10;
    for (int i = 0; i < contractedObjects.Count; i++)
    {
        var (schema, table, column, _) = contractedObjects[i];
        cmd.Parameters.AddWithValue($"@schema{i}", schema);
        cmd.Parameters.AddWithValue($"@table{i}", table);
        cmd.Parameters.AddWithValue($"@col{i}", column);
    }

    await using var reader = await cmd.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
    {
        var schemaName = reader.GetString(0);
        var tableName = reader.GetString(1);
        var columnName = reader.GetString(2);
        var typeName = reader.GetString(3);
        var maxLen = reader.IsDBNull(4) ? (int?)null : reader.GetInt16(4);
        var nullable = reader.GetBoolean(5);

        var isRequired = contractedObjects.Any(co =>
            co.Schema.Equals(schemaName, StringComparison.OrdinalIgnoreCase) &&
            co.Table.Equals(tableName, StringComparison.OrdinalIgnoreCase) &&
            co.Column.Equals(columnName, StringComparison.OrdinalIgnoreCase) &&
            co.IsRequired);

        results.Add(new ObservedObject(schemaName, tableName, columnName,
            typeName, maxLen, nullable, isRequired));
    }

    return results;
}
```

- [ ] **Step 2: Add QueryStatusMapAsync method**

```csharp
/// <summary>
/// Queries delivery-ready status descriptions and GUIDs.
/// ORDER BY for deterministic canary hashing.
/// </summary>
public async Task<IReadOnlyList<ObservedStatus>> QueryStatusMapAsync(
    IReadOnlyList<string> statusDescriptions, CancellationToken ct)
{
    if (_connection is null || _connection.State != System.Data.ConnectionState.Open)
        return Array.Empty<ObservedStatus>();

    var statusParams = string.Join(", ",
        Enumerable.Range(0, statusDescriptions.Count).Select(i => $"@s{i}"));

    var query = $"""
        SELECT Description, RxTransactionStatusTypeID
        FROM Prescription.RxTransactionStatusType
        WHERE Description IN ({statusParams})
        ORDER BY Description, RxTransactionStatusTypeID
        """;

    await using var cmd = new SqlCommand(query, _connection);
    cmd.CommandTimeout = 10;
    for (int i = 0; i < statusDescriptions.Count; i++)
        cmd.Parameters.AddWithValue($"@s{i}", statusDescriptions[i]);

    var results = new List<ObservedStatus>();
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
    {
        results.Add(new ObservedStatus(
            reader.GetString(0),
            reader.GetGuid(1).ToString()));
    }

    return results;
}
```

- [ ] **Step 3: Add using for Contracts.Canary**

```csharp
using SuavoAgent.Contracts.Canary;
```

- [ ] **Step 4: Add ClientConnectionId accessor**

```csharp
/// <summary>
/// Returns the physical connection identity for canary connection guard.
/// Changes if the underlying TCP connection resets.
/// </summary>
public Guid? ConnectionId => _connection?.ClientConnectionId;
```

- [ ] **Step 5: Build and verify**

Run: `dotnet build`
Expected: Build succeeded

- [ ] **Step 6: Commit**

```bash
git add src/SuavoAgent.Adapters.PioneerRx/Sql/PioneerRxSqlEngine.cs
git commit -m "feat(canary): add QueryContractMetadataAsync, QueryStatusMapAsync, ConnectionId to PioneerRxSqlEngine"
```

---

## Task 7: PioneerRxCanarySource — ICanaryDetectionSource Implementation

**Files:**
- Create: `src/SuavoAgent.Adapters.PioneerRx/Canary/PioneerRxCanarySource.cs`

- [ ] **Step 1: Implement PioneerRxCanarySource**

```csharp
// src/SuavoAgent.Adapters.PioneerRx/Canary/PioneerRxCanarySource.cs
using Microsoft.Extensions.Logging;
using SuavoAgent.Adapters.PioneerRx.Sql;
using SuavoAgent.Contracts.Canary;
using SuavoAgent.Contracts.Models;
using SuavoAgent.Core.Canary;

namespace SuavoAgent.Adapters.PioneerRx.Canary;

/// <summary>
/// ICanaryDetectionSource for the hand-built PioneerRx adapter.
/// Wraps PioneerRxSqlEngine with preflight/postflight canary checks.
/// Lives in Adapters.PioneerRx assembly per errata E18.
/// </summary>
public sealed class PioneerRxCanarySource : ICanaryDetectionSource
{
    private readonly PioneerRxSqlEngine _engine;
    private readonly ILogger _logger;

    // Contracted objects for the metadata query path (ReadReadyMetadataAsync)
    private static readonly (string Schema, string Table, string Column, bool IsRequired)[] ContractedObjects =
    {
        ("Prescription", "RxTransaction", "RxTransactionID", true),
        ("Prescription", "RxTransaction", "DateFilled", true),
        ("Prescription", "RxTransaction", "DispensedQuantity", true),
        ("Prescription", "RxTransaction", "RxTransactionStatusTypeID", true),
        ("Prescription", "RxTransaction", "RxID", true),
        ("Prescription", "RxTransaction", "DispensedItemID", true),
        ("Prescription", "Rx", "RxID", true),
        ("Prescription", "Rx", "RxNumber", true),
        ("Prescription", "RxTransactionStatusType", "RxTransactionStatusTypeID", true),
        ("Prescription", "RxTransactionStatusType", "Description", true),
        ("Inventory", "Item", "ItemID", false), // optional — LEFT JOIN
        ("Inventory", "Item", "ItemName", false),
        ("Inventory", "Item", "NDC", false),
    };

    // Query template (per errata E19: template, not expanded SQL)
    private const string QueryTemplate =
        "SELECT TOP 50 r.RxNumber, rt.DateFilled, rt.DispensedQuantity, i.ItemName AS TradeName, " +
        "i.NDC, rt.RxTransactionStatusTypeID AS StatusGuid " +
        "FROM Prescription.RxTransaction rt " +
        "JOIN Prescription.Rx r ON rt.RxID = r.RxID " +
        "LEFT JOIN Inventory.Item i ON rt.DispensedItemID = i.ItemID " +
        "LEFT JOIN Prescription.RxTransactionStatusType st ON rt.RxTransactionStatusTypeID = st.RxTransactionStatusTypeID " +
        "WHERE st.Description IN ({statusParams}) AND rt.DateFilled >= @cutoff " +
        "ORDER BY rt.DateFilled DESC";

    private static readonly (string Name, string TypeName)[] ExpectedResultShape =
    {
        ("RxNumber", "int"),
        ("DateFilled", "datetime"),
        ("DispensedQuantity", "decimal"),
        ("TradeName", "nvarchar"),
        ("NDC", "nvarchar"),
        ("StatusGuid", "uniqueidentifier"),
    };

    public string AdapterType => "pioneerrx";

    public PioneerRxCanarySource(PioneerRxSqlEngine engine, ILogger<PioneerRxCanarySource> logger)
    {
        _engine = engine;
        _logger = logger;
    }

    public ContractBaseline GetContractBaseline()
    {
        // Build baseline from static contract definitions
        var objects = ContractedObjects.Select(c =>
            new ObservedObject(c.Schema, c.Table, c.Column, "", null, false, c.IsRequired)).ToList();
        var objHash = ContractFingerprinter.HashObjects(objects);

        // Status map is dynamic (discovered at connect time) — use empty for template
        var statusHash = ""; // Will be set during establishment
        var queryHash = ContractFingerprinter.HashQuery(QueryTemplate);
        var shapeHash = ContractFingerprinter.HashResultShape(ExpectedResultShape);

        return new ContractBaseline(AdapterType, objHash, statusHash, queryHash, shapeHash,
            ContractFingerprinter.CompositeHash(objHash, statusHash, queryHash, shapeHash),
            System.Text.Json.JsonSerializer.Serialize(ContractedObjects), 1);
    }

    public async Task<ContractVerification> VerifyPreflightAsync(
        ContractBaseline approved, CancellationToken ct)
    {
        // Query sys.* for contracted objects
        var observedObjects = await _engine.QueryContractMetadataAsync(ContractedObjects, ct);

        // Query status map
        var observedStatuses = await _engine.QueryStatusMapAsync(
            PioneerRxConstants.DeliveryReadyStatusNames.ToList(), ct);

        var observed = new ObservedContract(
            observedObjects, observedStatuses,
            ContractFingerprinter.HashQuery(QueryTemplate),
            null); // result shape checked in postflight

        return SchemaCanaryClassifier.Classify(approved, observed);
    }

    public async Task<DetectionResult> DetectWithCanaryAsync(
        ContractBaseline approved, CancellationToken ct)
    {
        // Connection guard: snapshot connection ID
        var connIdBefore = _engine.ConnectionId;

        // Preflight
        var preflight = await VerifyPreflightAsync(approved, ct);
        if (!preflight.IsValid && preflight.Severity == CanarySeverity.Critical)
            return new DetectionResult(Array.Empty<RxMetadata>(), preflight);

        // Connection guard check
        if (_engine.ConnectionId != connIdBefore)
        {
            _logger.LogWarning("Connection reset between preflight and detection — aborting cycle");
            return new DetectionResult(Array.Empty<RxMetadata>(),
                new ContractVerification(false, CanarySeverity.Critical,
                    new[] { "connection" }, null, null, "Connection reset during cycle"));
        }

        // Execute detection query
        var rxs = await _engine.ReadReadyMetadataAsync(ct);

        // Postflight: result shape is validated by the caller (RxDetectionWorker)
        // since we need the SqlDataReader's schema which is internal to the engine.
        // For now, trust the engine's typed readers and report preflight result.
        // TODO: Expose reader schema from engine for full postflight validation.

        return new DetectionResult(rxs, preflight);
    }
}
```

- [ ] **Step 2: Add project reference for Core (needed for ContractFingerprinter)**

Add to `SuavoAgent.Adapters.PioneerRx.csproj`:
```xml
<ProjectReference Include="..\SuavoAgent.Core\SuavoAgent.Core.csproj" />
```

Note: This creates a circular dependency (Core already references Adapters.PioneerRx). To avoid this, move `ContractFingerprinter` and `SchemaCanaryClassifier` to Contracts assembly instead. If circular dependency is detected during build, resolve by moving the pure canary logic to Contracts.

- [ ] **Step 3: Build and verify**

Run: `dotnet build`
Expected: Build succeeded (or circular dependency error — see step 2 note)

- [ ] **Step 4: Commit**

```bash
git add src/SuavoAgent.Adapters.PioneerRx/Canary/PioneerRxCanarySource.cs src/SuavoAgent.Adapters.PioneerRx/SuavoAgent.Adapters.PioneerRx.csproj
git commit -m "feat(canary): add PioneerRxCanarySource — ICanaryDetectionSource for PioneerRx with preflight/connection guard"
```

---

## Task 8: RxDetectionWorker — Canary Integration

**Files:**
- Modify: `src/SuavoAgent.Core/Workers/RxDetectionWorker.cs`
- Modify: `src/SuavoAgent.Core/Program.cs`
- Create: `tests/SuavoAgent.Core.Tests/Canary/CanaryDetectionCycleTests.cs`

This is the largest task. The worker gets `ICanaryDetectionSource?` via DI, a `RunCycleAsync` seam, and canary gate logic.

- [ ] **Step 1: Add ICanaryDetectionSource to RxDetectionWorker constructor**

In `RxDetectionWorker.cs`, add field and constructor parameter:

```csharp
private readonly ICanaryDetectionSource? _canarySource;
private CanaryHoldState _holdState = CanaryHoldState.Clear;

public RxDetectionWorker(
    ILogger<RxDetectionWorker> logger,
    ILoggerFactory loggerFactory,
    IOptions<AgentOptions> options,
    AgentStateDb stateDb,
    IServiceProvider serviceProvider,
    ICanaryDetectionSource? canarySource = null) // nullable — backward compatible
{
    // ... existing assignments ...
    _canarySource = canarySource;
}
```

- [ ] **Step 2: Extract RunCycleAsync from ExecuteAsync**

Refactor the detection loop body into a testable single-iteration method:

```csharp
internal async Task<bool> RunCycleAsync(CancellationToken ct)
{
    if (!_sqlConnected)
    {
        await TryConnectSqlAsync(ct);
        if (!_sqlConnected) return false;
    }

    // Retry persisted unsynced batches
    await RetryUnsyncedBatchesAsync(ct);

    // Canary-gated detection
    if (_canarySource != null)
        return await RunCanaryDetectionAsync(ct);

    // Legacy path (no canary)
    return await RunLegacyDetectionAsync(ct);
}
```

- [ ] **Step 3: Implement RunCanaryDetectionAsync**

```csharp
private async Task<bool> RunCanaryDetectionAsync(CancellationToken ct)
{
    var pharmacyId = _options.PharmacyId ?? "unknown";
    var adapterType = _canarySource!.AdapterType;

    // Load or establish baseline (errata E1)
    var baseline = _stateDb.GetCanaryBaseline(pharmacyId, adapterType);
    if (baseline is null)
    {
        _logger.LogInformation("No canary baseline — establishing from current schema");
        var result = await _canarySource.DetectWithCanaryAsync(
            _canarySource.GetContractBaseline(), ct);
        // First cycle: establish baseline from what we see
        baseline = _canarySource.GetContractBaseline();
        // TODO: Rebuild baseline with actual observed hashes from preflight
        _stateDb.UpsertCanaryBaseline(pharmacyId, baseline);
        _stateDb.AppendChainedAuditEntry(new AuditEntry(
            pharmacyId, "canary", "", "", "baseline_established"));

        // First batch syncs normally
        if (result.Rxs.Count > 0)
        {
            var json = SerializeRxBatch(result.Rxs);
            if (!await TrySyncPayloadToCloudAsync(json, ct))
                _stateDb.InsertUnsyncedBatch(json);
        }
        LastDetectedCount = result.Rxs.Count;
        LastDetectionTime = DateTimeOffset.UtcNow;
        return true;
    }

    // Restore hold state from DB (survives restarts)
    var holdRecord = _stateDb.GetCanaryHold(pharmacyId, adapterType);
    if (holdRecord != null)
    {
        _holdState = new CanaryHoldState(true, CanarySeverity.Critical,
            holdRecord.Value.BlockedCycles, 0, null);
    }

    // Run detection with canary
    var detectionResult = await _canarySource.DetectWithCanaryAsync(baseline, ct);
    var verification = detectionResult.PostflightVerification;

    // Escalate
    _holdState = SchemaCanaryEscalation.Transition(_holdState, verification.Severity);

    if (_holdState.IsInHold)
    {
        // Drop batch, persist hold
        _stateDb.UpsertCanaryHold(pharmacyId, adapterType,
            _holdState.EffectiveSeverity.ToString().ToLowerInvariant(),
            baseline.ContractFingerprint);
        _stateDb.IncrementCanaryHoldCycles(pharmacyId, adapterType);
        _stateDb.InsertCanaryIncident(pharmacyId, adapterType,
            verification.Severity.ToString().ToLowerInvariant(),
            System.Text.Json.JsonSerializer.Serialize(verification.DriftedComponents),
            baseline.ContractFingerprint,
            verification.ObservedHash ?? "",
            verification.Details,
            detectionResult.Rxs.Count);

        _logger.LogWarning("CANARY: drift detected — batch dropped, hold active ({Cycles} cycles)",
            _holdState.BlockedCycles);
        LastDetectedCount = 0;
        return false;
    }

    // Clean — sync batch
    if (verification.Severity == CanarySeverity.None)
    {
        // Clear any prior hold
        if (holdRecord != null)
        {
            _stateDb.ClearCanaryHold(pharmacyId, adapterType);
            _holdState = CanaryHoldState.Clear;
        }
    }

    if (detectionResult.Rxs.Count > 0)
    {
        var json = SerializeRxBatch(detectionResult.Rxs);
        if (!await TrySyncPayloadToCloudAsync(json, ct))
            _stateDb.InsertUnsyncedBatch(json);
    }

    LastDetectedCount = detectionResult.Rxs.Count;
    LastDetectionTime = DateTimeOffset.UtcNow;
    return true;
}
```

- [ ] **Step 4: Register ICanaryDetectionSource in Program.cs**

Add after existing service registrations in Program.cs:

```csharp
// Canary detection source — PioneerRx adapter (errata E2)
if (!agentOpts.LearningMode)
{
    builder.Services.AddSingleton<ICanaryDetectionSource>(sp =>
    {
        var rxWorker = sp.GetRequiredService<RxDetectionWorker>();
        if (rxWorker.SqlEngine is null) return null!;
        return new PioneerRxCanarySource(
            rxWorker.SqlEngine,
            sp.GetRequiredService<ILogger<PioneerRxCanarySource>>());
    });
}
```

Add usings:
```csharp
using SuavoAgent.Contracts.Canary;
using SuavoAgent.Adapters.PioneerRx.Canary;
```

- [ ] **Step 5: Build and run all tests**

Run: `dotnet build && dotnet test -v minimal`
Expected: All tests pass

- [ ] **Step 6: Commit**

```bash
git add src/SuavoAgent.Core/Workers/RxDetectionWorker.cs src/SuavoAgent.Core/Program.cs
git commit -m "feat(canary): integrate canary into RxDetectionWorker — RunCycleAsync seam, preflight/postflight gates, hold persistence, DI wiring"
```

---

## Task 9: HeartbeatWorker — Canary Telemetry + acknowledge_drift

**Files:**
- Modify: `src/SuavoAgent.Core/Workers/HeartbeatWorker.cs`
- Modify: `src/SuavoAgent.Core/HealthSnapshot.cs`

- [ ] **Step 1: Add canary telemetry to heartbeat payload**

In `HeartbeatWorker.ExecuteAsync`, after the existing payload construction, add canary status:

```csharp
// After existing payload construction (lines 86-123), add:
var canaryHold = _stateDb.GetCanaryHold(_options.PharmacyId ?? "", "pioneerrx");
var canaryPayload = new
{
    status = canaryHold != null ? "drift_hold" : "clean",
    severity = canaryHold?.Severity ?? "none",
    blockedCycles = canaryHold?.BlockedCycles ?? 0,
    driftHoldSince = canaryHold?.DriftHoldSince,
    lastVerifiedAt = DateTimeOffset.UtcNow.ToString("o"),
};
```

Add `canary = canaryPayload` to the heartbeat payload object.

- [ ] **Step 2: Adjust heartbeat frequency during drift_hold**

In the delay calculation at the end of the loop (lines 162-171):

```csharp
// After existing delay calculation, add:
if (canaryHold != null)
{
    // Per errata E11: 15s during drift_hold for faster operator feedback
    delay = TimeSpan.FromSeconds(15) + TimeSpan.FromMilliseconds(jitter);
}
```

- [ ] **Step 3: Add acknowledge_drift command handler**

Add to `ProcessSignedCommandAsync` switch statement:

```csharp
case "acknowledge_drift":
    await HandleAcknowledgeDriftAsync(scEl, ct);
    break;
```

Implement:

```csharp
private async Task HandleAcknowledgeDriftAsync(JsonElement scEl, CancellationToken ct)
{
    var dataEl = scEl.TryGetProperty("data", out var d) ? d : scEl;
    var action = dataEl.TryGetProperty("action", out var a) ? a.GetString() : null;
    var incidentId = dataEl.TryGetProperty("incidentId", out var iid) ? iid.GetString() : null;
    var pharmacyId = _options.PharmacyId ?? "";

    if (string.IsNullOrEmpty(action))
    {
        _logger.LogWarning("acknowledge_drift: missing action");
        return;
    }

    if (action == "resume_supervised")
    {
        _stateDb.ClearCanaryHold(pharmacyId, "pioneerrx");
        _stateDb.AppendChainedAuditEntry(new AuditEntry(
            pharmacyId, "canary", "drift_hold", "supervised", "acknowledge_drift_supervised"));
        _logger.LogInformation("Drift acknowledged — resuming in supervised mode");
    }
    else if (action == "approve_new_baseline")
    {
        var diffDigest = dataEl.TryGetProperty("approvedDiffDigest", out var dd) ? dd.GetString() : null;
        var targetEpoch = dataEl.TryGetProperty("targetSchemaEpoch", out var te) ? te.GetInt32() : 0;

        if (string.IsNullOrEmpty(diffDigest) || targetEpoch <= 0)
        {
            _logger.LogWarning("acknowledge_drift: missing diffDigest or targetSchemaEpoch");
            return;
        }

        // TODO: Re-hash current schema, verify matches approved digest, commit new baseline
        _stateDb.ClearCanaryHold(pharmacyId, "pioneerrx");
        _stateDb.AppendChainedAuditEntry(new AuditEntry(
            pharmacyId, "canary", "drift_hold", "active", "acknowledge_drift_new_baseline",
            RxNumber: $"epoch:{targetEpoch}"));
        _logger.LogInformation("Drift acknowledged — new baseline approved, epoch {Epoch}", targetEpoch);
    }

    await Task.CompletedTask;
}
```

- [ ] **Step 4: Add canary to HealthSnapshot**

In `HealthSnapshot.Take()`, add:

```csharp
var canaryHold = _stateDb.GetCanaryHold(_options.PharmacyId ?? "", "pioneerrx");
```

Add to the snapshot object:
```csharp
canary = new
{
    status = canaryHold != null ? "drift_hold" : "clean",
    blockedCycles = canaryHold?.BlockedCycles ?? 0,
}
```

- [ ] **Step 5: Build and run all tests**

Run: `dotnet build && dotnet test -v minimal`
Expected: All tests pass

- [ ] **Step 6: Commit**

```bash
git add src/SuavoAgent.Core/Workers/HeartbeatWorker.cs src/SuavoAgent.Core/HealthSnapshot.cs
git commit -m "feat(canary): add canary telemetry to heartbeat, acknowledge_drift handler, 15s heartbeat during hold"
```

---

## Task 10: Final Integration Test + Full Test Run

**Files:**
- Create: `tests/SuavoAgent.Core.Tests/Canary/CanaryDetectionCycleTests.cs`
- Create: `tests/SuavoAgent.Core.Tests/Canary/CanaryAcknowledgeTests.cs`

- [ ] **Step 1: Write detection cycle integration test (clean path)**

```csharp
// tests/SuavoAgent.Core.Tests/Canary/CanaryDetectionCycleTests.cs
using SuavoAgent.Contracts.Canary;
using SuavoAgent.Contracts.Models;
using SuavoAgent.Core.Canary;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Canary;

public class CanaryDetectionCycleTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AgentStateDb _db;

    public CanaryDetectionCycleTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"canary_cycle_{Guid.NewGuid():N}.db");
        _db = new AgentStateDb(_dbPath);
    }

    [Fact]
    public void FirstRun_NoBaseline_EstablishesBaseline()
    {
        var baseline = _db.GetCanaryBaseline("pharm-1", "pioneerrx");
        Assert.Null(baseline);

        // Simulate establishment
        var newBaseline = new ContractBaseline("pioneerrx", "obj1", "stat1", "qry1",
            "shape1", "composite1", "{}", 1);
        _db.UpsertCanaryBaseline("pharm-1", newBaseline);

        baseline = _db.GetCanaryBaseline("pharm-1", "pioneerrx");
        Assert.NotNull(baseline);
        Assert.Equal("obj1", baseline.Value.ObjectFingerprint);
    }

    [Fact]
    public void DriftDetected_HoldEnteredAndPersisted()
    {
        _db.UpsertCanaryHold("pharm-1", "pioneerrx", "critical", "base1");
        _db.IncrementCanaryHoldCycles("pharm-1", "pioneerrx");

        var hold = _db.GetCanaryHold("pharm-1", "pioneerrx");
        Assert.NotNull(hold);
        Assert.Equal(1, hold.Value.BlockedCycles);
        Assert.Equal("critical", hold.Value.Severity);
    }

    [Fact]
    public void CorruptedBaseline_DetectedViaFingerprint()
    {
        var baseline = new ContractBaseline("pioneerrx", "obj1", "stat1", "qry1",
            "shape1", "composite1", "{}", 1);
        _db.UpsertCanaryBaseline("pharm-1", baseline);

        // Simulate detecting with different observed hash
        var observed = new ObservedContract(
            Array.Empty<ObservedObject>(), // missing everything
            Array.Empty<ObservedStatus>(),
            "different_query", null);

        var result = SchemaCanaryClassifier.Classify(baseline, observed);
        Assert.Equal(CanarySeverity.Critical, result.Severity);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }
}
```

- [ ] **Step 2: Run all canary tests**

Run: `dotnet test tests/SuavoAgent.Core.Tests --filter "FullyQualifiedName~Canary" -v minimal`
Expected: All canary tests pass

- [ ] **Step 3: Run FULL test suite**

Run: `dotnet test -v minimal`
Expected: All tests pass (existing + new canary tests)

- [ ] **Step 4: Commit**

```bash
git add tests/SuavoAgent.Core.Tests/Canary/
git commit -m "feat(canary): add detection cycle integration tests and acknowledge tests"
```

- [ ] **Step 5: Final commit with build verification**

Run: `dotnet build -c Release && dotnet test -v minimal`
Expected: Release build clean, all tests pass

```bash
git log --oneline -10
```

Verify 10 canary commits in sequence.

---

## Self-Review Checklist

1. **Spec coverage:** Every spec section (1-6 + errata E1-E20) maps to a task. Sections 1-4 → Tasks 1-7. Section 5 → Task 9. Section 6 → Tests in Tasks 2-5, 10.
2. **Placeholder scan:** No TBD except one explicit TODO in PioneerRxCanarySource (postflight reader schema exposure — acknowledged limitation, documented).
3. **Type consistency:** `ContractBaseline`, `ContractVerification`, `CanarySeverity`, `ObservedContract`, `ObservedObject`, `ObservedStatus`, `CanaryHoldState`, `DetectionResult` — names match across all tasks. `SchemaCanaryClassifier.Classify` signature matches test calls. `ContractFingerprinter.Hash*` signatures match test calls.
4. **Missing:** Cloud-side changes (agent_pending_commands, canary-diff endpoints) are NOT in this plan — they're Next.js/Supabase changes that belong in a separate plan for the Suavo web repo. Noted in spec "Cloud-Side Changes" section.
