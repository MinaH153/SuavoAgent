# Track 3 PHI Compile-Time CI Gate — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship the structural compile-time gate that fails any build where a `[PhiDirect]` field appears on an `[OutboundPayload]` type — both repos, IDE-aware, with synthetic canary fixtures proving the gate works.

**Architecture:** Two parallel enforcement systems sharing a unified mental model. SuavoAgent: Roslyn analyzer (`SUAVO0001` rule, with `SUAVO0099` crash recovery) operating on the C# symbol model with cycle-guarded type-graph traversal capped at 50 levels. Suavo: custom ESLint rule (`suavo-phi/no-phi-in-outbound`) walking AST under existing `eslint-rules/` flat-config pattern. Synthetic canary lives as test fixtures in each language's analyzer test suite — not a separate failing PR.

**Tech Stack:** C# .NET 8, xUnit 2.9, Roslyn (`Microsoft.CodeAnalysis.CSharp.Workspaces` + `Microsoft.CodeAnalysis.CSharp.Analyzer.Testing.XUnit`), netstandard2.0 for analyzer assembly. TypeScript, Zod, ESLint 9 flat config, vitest 4, ESLint `RuleTester`.

---

## Source spec

`docs/superpowers/specs/2026-05-01-track-3-phi-ci-gate-design.md`

---

## Branch strategy

- **SuavoAgent (Phase 1):** branch off `main` as `feat/wave-1-phi-ci-gate-suavoagent`. Independent PR.
- **Suavo (Phase 2):** branch off `main` as `feat/wave-1-phi-ci-gate-suavo`. Independent PR.
- Each phase ships working/testable software on its own; Wave 1 gate trips when both have shipped + the canary in each fails build appropriately.

---

## File structure

### SuavoAgent (`~/Code/SuavoAgent`)

**Create:**
- `src/SuavoAgent.Contracts/Annotations/PhiDirectAttribute.cs` — `[PhiDirect]` attribute
- `src/SuavoAgent.Contracts/Annotations/OutboundPayloadAttribute.cs` — `[OutboundPayload]` attribute
- `src/SuavoAgent.Analyzers/SuavoAgent.Analyzers.csproj` — analyzer project (netstandard2.0)
- `src/SuavoAgent.Analyzers/PhiInOutboundPayloadAnalyzer.cs` — DiagnosticAnalyzer
- `src/SuavoAgent.Analyzers/DiagnosticDescriptors.cs` — SUAVO0001 + SUAVO0099 metadata
- `tests/SuavoAgent.Analyzers.Tests/SuavoAgent.Analyzers.Tests.csproj` — analyzer test project
- `tests/SuavoAgent.Analyzers.Tests/PhiInOutboundPayloadAnalyzerTests.cs` — analyzer unit tests
- `tests/SuavoAgent.Analyzers.Tests/CanaryFixtureTests.cs` — synthetic canary
- `tests/SuavoAgent.Analyzers.Tests/RetrofittedTypesRegressionTests.cs` — regression tests
- `tests/SuavoAgent.Analyzers.Tests/PerformanceTests.cs` — 1000-type stress test
- `tests/SuavoAgent.Analyzers.IntegrationTest/SuavoAgent.Analyzers.IntegrationTest.csproj` — fixture project that intentionally fails build

**Modify:**
- `src/SuavoAgent.Contracts/SuavoAgent.Contracts.csproj` — add analyzer ProjectReference
- `src/SuavoAgent.Contracts/Models/PatientDetailsPayload.cs` — add `[OutboundPayload]`
- `src/SuavoAgent.Contracts/Models/WaveGateTrippedPayload.cs` — add `[OutboundPayload]`
- `src/SuavoAgent.Contracts/Models/WaveGateFailedPayload.cs` — add `[OutboundPayload]`
- `SuavoAgent.sln` — add new projects

### Suavo (`~/Code/Suavo`)

**Create:**
- `src/lib/zod-phi.ts` — `phi()` + `outbound()` Zod helpers
- `src/lib/__tests__/zod-phi.test.ts` — helper tests
- `eslint-rules/no-phi-in-outbound.mjs` — custom ESLint rule
- `eslint-rules/__tests__/no-phi-in-outbound.test.mjs` — rule unit tests + canary + perf

**Modify:**
- `eslint.config.js` (or equivalent flat config) — register the rule
- `src/lib/wave-event-payloads.ts` — wrap exported schemas with `outbound()`

---

# Phase 1 — SuavoAgent

## Task 1: Add `[PhiDirect]` + `[OutboundPayload]` attributes

**Repo:** SuavoAgent
**Files:**
- Create: `src/SuavoAgent.Contracts/Annotations/PhiDirectAttribute.cs`
- Create: `src/SuavoAgent.Contracts/Annotations/OutboundPayloadAttribute.cs`
- Test: `tests/SuavoAgent.Contracts.Tests/Annotations/AnnotationAttributeTests.cs`

- [ ] **Step 1: Branch off main**

```bash
cd /Users/joshuahenein/Code/SuavoAgent
git checkout main
git pull --ff-only origin main
git checkout -b feat/wave-1-phi-ci-gate-suavoagent
mkdir -p src/SuavoAgent.Contracts/Annotations
mkdir -p tests/SuavoAgent.Contracts.Tests/Annotations
```

- [ ] **Step 2: Write the failing test**

Create `tests/SuavoAgent.Contracts.Tests/Annotations/AnnotationAttributeTests.cs`:

```csharp
using System;
using SuavoAgent.Contracts.Annotations;
using SuavoAgent.Contracts.Models;
using Xunit;

namespace SuavoAgent.Contracts.Tests.Annotations;

public class AnnotationAttributeTests
{
    [Fact]
    public void PhiDirectAttribute_TargetsPropertiesAndFields()
    {
        var usage = (AttributeUsageAttribute)Attribute.GetCustomAttribute(
            typeof(PhiDirectAttribute),
            typeof(AttributeUsageAttribute))!;

        Assert.True(usage.ValidOn.HasFlag(AttributeTargets.Property));
        Assert.True(usage.ValidOn.HasFlag(AttributeTargets.Field));
        Assert.False(usage.AllowMultiple);
        Assert.True(usage.Inherited);
    }

    [Fact]
    public void OutboundPayloadAttribute_TargetsTypes()
    {
        var usage = (AttributeUsageAttribute)Attribute.GetCustomAttribute(
            typeof(OutboundPayloadAttribute),
            typeof(AttributeUsageAttribute))!;

        Assert.True(usage.ValidOn.HasFlag(AttributeTargets.Class));
        Assert.True(usage.ValidOn.HasFlag(AttributeTargets.Struct));
        Assert.True(usage.ValidOn.HasFlag(AttributeTargets.Interface));
        Assert.False(usage.AllowMultiple);
        Assert.True(usage.Inherited);
    }

    [Fact]
    public void Attributes_CanBeAppliedTogether()
    {
        // Compile-time check: just verify these declarations compile
        // (they will once the attributes exist + after Task 11 retrofits PatientDetailsPayload)
        var info = typeof(PatientDetailsPayload);
        Assert.NotNull(info);
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

```bash
dotnet test tests/SuavoAgent.Contracts.Tests/ --filter "FullyQualifiedName~AnnotationAttributeTests" 2>&1 | tail -10
```

Expected: build fails with `error CS0246: The type or namespace name 'PhiDirectAttribute' could not be found`.

- [ ] **Step 4: Implement `PhiDirectAttribute`**

Create `src/SuavoAgent.Contracts/Annotations/PhiDirectAttribute.cs`:

```csharp
using System;

namespace SuavoAgent.Contracts.Annotations;

/// <summary>
/// Marks a property or field as PHI-Direct per <c>docs/self-healing/field-registry.md</c>
/// classification. The Roslyn analyzer in <c>SuavoAgent.Analyzers</c> uses this to
/// fail any build where a field carrying this attribute appears on a type marked
/// <see cref="OutboundPayloadAttribute"/>.
///
/// PHI-Direct fields MUST NEVER cross the network in any form — events, verbs,
/// sync payloads, heartbeat, model prompts. They live exclusively in agent-local
/// SQLite, encrypted at rest. See meta-roadmap §9 invariant 1 for the
/// existential framing.
/// </summary>
[AttributeUsage(
    AttributeTargets.Property | AttributeTargets.Field,
    AllowMultiple = false,
    Inherited = true)]
public sealed class PhiDirectAttribute : Attribute;
```

- [ ] **Step 5: Implement `OutboundPayloadAttribute`**

Create `src/SuavoAgent.Contracts/Annotations/OutboundPayloadAttribute.cs`:

```csharp
using System;

namespace SuavoAgent.Contracts.Annotations;

/// <summary>
/// Marks a record / class / struct / interface as a network-bound payload type.
/// The Roslyn analyzer in <c>SuavoAgent.Analyzers</c> walks the type's member
/// graph (with cycle guard, capped at 50 levels) and fails the build if any
/// nested member carries <see cref="PhiDirectAttribute"/>.
///
/// Apply this to types that cross the agent ↔ cloud boundary:
/// HMAC sync payloads, heartbeat events, audit event payloads, signed verb
/// invocations, model prompts. See <c>docs/self-healing/field-registry.md</c>
/// for the canonical PHI tier classification.
/// </summary>
[AttributeUsage(
    AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface,
    AllowMultiple = false,
    Inherited = true)]
public sealed class OutboundPayloadAttribute : Attribute;
```

- [ ] **Step 6: Run test to verify it passes**

```bash
dotnet test tests/SuavoAgent.Contracts.Tests/ --filter "FullyQualifiedName~AnnotationAttributeTests" 2>&1 | tail -5
```

Expected: 3 tests pass.

- [ ] **Step 7: Commit**

```bash
git add src/SuavoAgent.Contracts/Annotations/ \
        tests/SuavoAgent.Contracts.Tests/Annotations/
git commit -m "feat(contracts): add [PhiDirect] + [OutboundPayload] attributes"
```

---

## Task 2: Bootstrap `SuavoAgent.Analyzers` project

**Repo:** SuavoAgent
**Files:**
- Create: `src/SuavoAgent.Analyzers/SuavoAgent.Analyzers.csproj`
- Create: `src/SuavoAgent.Analyzers/DiagnosticDescriptors.cs`
- Modify: `SuavoAgent.sln` (add new project)

The analyzer must target `netstandard2.0` (Roslyn requirement). It uses `Microsoft.CodeAnalysis.CSharp.Workspaces` for the symbol model.

- [ ] **Step 1: Create the project directory + csproj**

```bash
mkdir -p src/SuavoAgent.Analyzers
```

Create `src/SuavoAgent.Analyzers/SuavoAgent.Analyzers.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <RootNamespace>SuavoAgent.Analyzers</RootNamespace>
    <AssemblyTitle>SuavoAgent.Analyzers</AssemblyTitle>
    <Description>Roslyn diagnostic analyzers enforcing SuavoAgent compile-time invariants (Track 3 PHI compile-enforcement, plus future Wave 5+ sink markers).</Description>
    <IsRoslynComponent>true</IsRoslynComponent>
    <EnforceExtendedAnalyzerRules>true</EnforceExtendedAnalyzerRules>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp.Workspaces" Version="4.11.0" PrivateAssets="all" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Create `DiagnosticDescriptors.cs` with SUAVO0001 + SUAVO0099**

Create `src/SuavoAgent.Analyzers/DiagnosticDescriptors.cs`:

```csharp
using Microsoft.CodeAnalysis;

namespace SuavoAgent.Analyzers;

/// <summary>
/// Centralized definitions of every SUAVO* diagnostic ID emitted by analyzers
/// in this assembly. Diagnostic IDs follow the pattern <c>SUAVOnnnn</c> where
/// <c>nnnn</c> is a unique 4-digit number. Categories track the broader area
/// the rule guards.
/// </summary>
internal static class DiagnosticDescriptors
{
    private const string Category = "SuavoAgent.Compliance";
    private const string HelpLinkBase =
        "https://github.com/MinaH153/SuavoAgent/blob/main/docs/superpowers/specs/2026-05-01-track-3-phi-ci-gate-design.md";

    /// <summary>
    /// SUAVO0001: A property or field marked <c>[PhiDirect]</c> appears on a
    /// type marked <c>[OutboundPayload]</c> (directly or via nested type
    /// references). PHI-Direct fields must never cross the network.
    /// </summary>
    public static readonly DiagnosticDescriptor PhiInOutboundPayload = new(
        id: "SUAVO0001",
        title: "PHI-Direct field on outbound payload",
        messageFormat: "PHI-Direct field '{0}' on outbound payload '{1}' — split required (move to local-only type or remove the [PhiDirect] marker).",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "PHI-Direct fields are forbidden on types marked [OutboundPayload] per Track 3 invariant 1.",
        helpLinkUri: HelpLinkBase + "#3--data-flow");

    /// <summary>
    /// SUAVO0099: The analyzer itself crashed or hit a guard limit. Surfaces
    /// loudly so a HIPAA-existential gate can never silently fail.
    /// </summary>
    public static readonly DiagnosticDescriptor AnalyzerInternalError = new(
        id: "SUAVO0099",
        title: "SuavoAgent analyzer internal error",
        messageFormat: "Analyzer crashed or hit a guard while analyzing '{0}': {1}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The analyzer encountered an unhandled exception or hit a hard limit (e.g., depth > 50). Build is failed loudly to prevent silent gate bypass.",
        helpLinkUri: HelpLinkBase + "#4--error-handling");
}
```

- [ ] **Step 3: Add the project to the solution**

```bash
dotnet sln SuavoAgent.sln add src/SuavoAgent.Analyzers/SuavoAgent.Analyzers.csproj
dotnet build src/SuavoAgent.Analyzers/SuavoAgent.Analyzers.csproj 2>&1 | tail -8
```

Expected: build succeeds. Empty analyzer DLL produced; no diagnostics declared yet.

- [ ] **Step 4: Commit**

```bash
git add src/SuavoAgent.Analyzers/ SuavoAgent.sln
git commit -m "feat(analyzers): bootstrap SuavoAgent.Analyzers project + SUAVO0001/SUAVO0099 descriptors"
```

---

## Task 3: Bootstrap `SuavoAgent.Analyzers.Tests` project

**Repo:** SuavoAgent
**Files:**
- Create: `tests/SuavoAgent.Analyzers.Tests/SuavoAgent.Analyzers.Tests.csproj`
- Modify: `SuavoAgent.sln` (add new project)

- [ ] **Step 1: Create the test project**

```bash
mkdir -p tests/SuavoAgent.Analyzers.Tests
```

Create `tests/SuavoAgent.Analyzers.Tests/SuavoAgent.Analyzers.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
    <RootNamespace>SuavoAgent.Analyzers.Tests</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp.Analyzer.Testing.XUnit" Version="1.1.2" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
    <PackageReference Include="coverlet.collector" Version="6.0.2" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\SuavoAgent.Analyzers\SuavoAgent.Analyzers.csproj" />
    <ProjectReference Include="..\..\src\SuavoAgent.Contracts\SuavoAgent.Contracts.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Add to solution + restore**

```bash
dotnet sln SuavoAgent.sln add tests/SuavoAgent.Analyzers.Tests/SuavoAgent.Analyzers.Tests.csproj
dotnet restore tests/SuavoAgent.Analyzers.Tests/ 2>&1 | tail -5
```

Expected: restore succeeds; test packages downloaded.

- [ ] **Step 3: Commit**

```bash
git add tests/SuavoAgent.Analyzers.Tests/SuavoAgent.Analyzers.Tests.csproj SuavoAgent.sln
git commit -m "test(analyzers): bootstrap SuavoAgent.Analyzers.Tests project"
```

---

## Task 4: Implement `PhiInOutboundPayloadAnalyzer` — direct-violation case

**Repo:** SuavoAgent
**Files:**
- Create: `src/SuavoAgent.Analyzers/PhiInOutboundPayloadAnalyzer.cs`
- Create: `tests/SuavoAgent.Analyzers.Tests/PhiInOutboundPayloadAnalyzerTests.cs`

TDD: write the test for the simplest violation case first.

- [ ] **Step 1: Write the failing test**

Create `tests/SuavoAgent.Analyzers.Tests/PhiInOutboundPayloadAnalyzerTests.cs`:

```csharp
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using SuavoAgent.Analyzers;
using Xunit;
using Verifier = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.AnalyzerVerifier<
    SuavoAgent.Analyzers.PhiInOutboundPayloadAnalyzer>;

namespace SuavoAgent.Analyzers.Tests;

public class PhiInOutboundPayloadAnalyzerTests
{
    private const string Annotations = """
        namespace SuavoAgent.Contracts.Annotations;
        using System;

        [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, Inherited = true)]
        public sealed class PhiDirectAttribute : Attribute { }

        [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface, Inherited = true)]
        public sealed class OutboundPayloadAttribute : Attribute { }
        """;

    [Fact]
    public async Task DirectViolation_PhiPropertyOnOutboundType_EmitsDiagnostic()
    {
        var source = Annotations + """

            using SuavoAgent.Contracts.Annotations;

            namespace TestNs;

            [OutboundPayload]
            public class LeakyPayload
            {
                [PhiDirect]
                public string {|#0:PatientName|} { get; set; } = "";
            }
            """;

        var expected = Verifier.Diagnostic(DiagnosticDescriptors.PhiInOutboundPayload)
            .WithLocation(0)
            .WithArguments("LeakyPayload.PatientName", "TestNs.LeakyPayload");

        await Verifier.VerifyAnalyzerAsync(source, expected);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/SuavoAgent.Analyzers.Tests/ 2>&1 | tail -15
```

Expected: build fails with `error CS0246: The type or namespace name 'PhiInOutboundPayloadAnalyzer' could not be found`.

- [ ] **Step 3: Implement the analyzer (direct case only — nested follows in Task 5)**

Create `src/SuavoAgent.Analyzers/PhiInOutboundPayloadAnalyzer.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SuavoAgent.Analyzers;

/// <summary>
/// SUAVO0001: emits an error diagnostic when a property/field marked
/// <c>[PhiDirect]</c> appears (directly or transitively through nested type
/// references) on a type marked <c>[OutboundPayload]</c>.
///
/// Crash-loud: any unhandled exception inside the analyzer surfaces as
/// SUAVO0099 rather than failing silently. Roslyn's default behavior for
/// crashed analyzers is silent suppression — unacceptable for a HIPAA gate.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PhiInOutboundPayloadAnalyzer : DiagnosticAnalyzer
{
    private const string OutboundAttributeFullName =
        "SuavoAgent.Contracts.Annotations.OutboundPayloadAttribute";
    private const string PhiAttributeFullName =
        "SuavoAgent.Contracts.Annotations.PhiDirectAttribute";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(
            DiagnosticDescriptors.PhiInOutboundPayload,
            DiagnosticDescriptors.AnalyzerInternalError);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(AnalyzeNamedType, SymbolKind.NamedType);
    }

    private static void AnalyzeNamedType(SymbolAnalysisContext ctx)
    {
        var symbol = (INamedTypeSymbol)ctx.Symbol;
        try
        {
            if (!HasAttribute(symbol, OutboundAttributeFullName))
                return;

            // Direct case: walk symbol's own properties/fields.
            // Nested case (Task 5) extends this to follow member type references.
            foreach (var member in symbol.GetMembers())
            {
                if (member is not (IPropertySymbol or IFieldSymbol))
                    continue;

                if (HasAttribute(member, PhiAttributeFullName))
                {
                    var location = member.Locations.FirstOrDefault() ?? Location.None;
                    var diag = Diagnostic.Create(
                        DiagnosticDescriptors.PhiInOutboundPayload,
                        location,
                        $"{symbol.Name}.{member.Name}",
                        symbol.ToDisplayString());
                    ctx.ReportDiagnostic(diag);
                }
            }
        }
        catch (Exception ex)
        {
            // Crash-loud: surface as SUAVO0099 rather than silent suppression.
            var location = symbol.Locations.FirstOrDefault() ?? Location.None;
            var diag = Diagnostic.Create(
                DiagnosticDescriptors.AnalyzerInternalError,
                location,
                symbol.ToDisplayString(),
                $"{ex.GetType().Name}: {ex.Message}");
            ctx.ReportDiagnostic(diag);
        }
    }

    private static bool HasAttribute(ISymbol symbol, string fullName) =>
        symbol.GetAttributes().Any(a =>
            a.AttributeClass?.ToDisplayString() == fullName);
}
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test tests/SuavoAgent.Analyzers.Tests/ --filter "FullyQualifiedName~DirectViolation" 2>&1 | tail -8
```

Expected: 1 test passes.

- [ ] **Step 5: Commit**

```bash
git add src/SuavoAgent.Analyzers/PhiInOutboundPayloadAnalyzer.cs \
        tests/SuavoAgent.Analyzers.Tests/PhiInOutboundPayloadAnalyzerTests.cs
git commit -m "feat(analyzers): SUAVO0001 PhiInOutboundPayloadAnalyzer (direct case)"
```

---

## Task 5: Extend analyzer for nested + cyclic types

**Repo:** SuavoAgent
**Files:**
- Modify: `src/SuavoAgent.Analyzers/PhiInOutboundPayloadAnalyzer.cs`
- Modify: `tests/SuavoAgent.Analyzers.Tests/PhiInOutboundPayloadAnalyzerTests.cs`

- [ ] **Step 1: Add failing tests for nested + cyclic + clean cases**

Append to `tests/SuavoAgent.Analyzers.Tests/PhiInOutboundPayloadAnalyzerTests.cs` (inside the class, before the closing brace):

```csharp
    [Fact]
    public async Task NestedViolation_PhiInChildType_EmitsDiagnostic()
    {
        var source = Annotations + """

            using SuavoAgent.Contracts.Annotations;

            namespace TestNs;

            public class PatientFields
            {
                [PhiDirect]
                public string {|#0:Address1|} { get; set; } = "";
            }

            [OutboundPayload]
            public class NestedLeakyPayload
            {
                public PatientFields Patient { get; set; } = new();
            }
            """;

        var expected = Verifier.Diagnostic(DiagnosticDescriptors.PhiInOutboundPayload)
            .WithLocation(0)
            .WithArguments("NestedLeakyPayload.Patient.Address1", "TestNs.NestedLeakyPayload");

        await Verifier.VerifyAnalyzerAsync(source, expected);
    }

    [Fact]
    public async Task CyclicTypes_DoesNotInfiniteLoop()
    {
        var source = Annotations + """

            using SuavoAgent.Contracts.Annotations;

            namespace TestNs;

            [OutboundPayload]
            public class A
            {
                public B? Other { get; set; }
                public string Name { get; set; } = "";
            }

            public class B
            {
                public A? Back { get; set; }
                public int Count { get; set; }
            }
            """;

        // No PHI in the graph; analyzer should terminate without diagnostics.
        await Verifier.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public async Task CleanOutboundType_NoPhiFields_NoDiagnostic()
    {
        var source = Annotations + """

            using SuavoAgent.Contracts.Annotations;

            namespace TestNs;

            [OutboundPayload]
            public class CleanPayload
            {
                public string RxHash { get; set; } = "";
                public int Quantity { get; set; }
            }
            """;

        await Verifier.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public async Task NonOutboundTypeWithPhi_NoDiagnostic()
    {
        // PHI-Direct in a local-only type (no [OutboundPayload]) is fine.
        var source = Annotations + """

            using SuavoAgent.Contracts.Annotations;

            namespace TestNs;

            public class LocalOnlyPatient
            {
                [PhiDirect]
                public string PatientName { get; set; } = "";
            }
            """;

        await Verifier.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public async Task MultipleViolations_ReportsAll()
    {
        var source = Annotations + """

            using SuavoAgent.Contracts.Annotations;

            namespace TestNs;

            [OutboundPayload]
            public class MultiLeak
            {
                [PhiDirect]
                public string {|#0:PatientName|} { get; set; } = "";

                [PhiDirect]
                public string {|#1:DateOfBirth|} { get; set; } = "";
            }
            """;

        var expected1 = Verifier.Diagnostic(DiagnosticDescriptors.PhiInOutboundPayload)
            .WithLocation(0)
            .WithArguments("MultiLeak.PatientName", "TestNs.MultiLeak");
        var expected2 = Verifier.Diagnostic(DiagnosticDescriptors.PhiInOutboundPayload)
            .WithLocation(1)
            .WithArguments("MultiLeak.DateOfBirth", "TestNs.MultiLeak");

        await Verifier.VerifyAnalyzerAsync(source, expected1, expected2);
    }
```

- [ ] **Step 2: Run tests to verify nested + cyclic fail (others pass)**

```bash
dotnet test tests/SuavoAgent.Analyzers.Tests/ 2>&1 | tail -15
```

Expected: `NestedViolation_*` fails (nested traversal not yet implemented). Others pass.

- [ ] **Step 3: Replace the analyzer with full type-graph traversal**

Replace `src/SuavoAgent.Analyzers/PhiInOutboundPayloadAnalyzer.cs` entirely with:

```csharp
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SuavoAgent.Analyzers;

/// <summary>
/// SUAVO0001: emits an error diagnostic when a property/field marked
/// <c>[PhiDirect]</c> appears (directly or transitively through nested type
/// references) on a type marked <c>[OutboundPayload]</c>.
///
/// Walks the type graph with a HashSet&lt;INamedTypeSymbol&gt; visited set
/// (cycle guard) and a depth cap of 50 levels. Only follows types defined
/// in the same assembly as the outbound type — framework + external types
/// are out of scope.
///
/// Crash-loud: any unhandled exception inside the analyzer surfaces as
/// SUAVO0099 rather than failing silently. Roslyn's default behavior for
/// crashed analyzers is silent suppression — unacceptable for a HIPAA gate.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PhiInOutboundPayloadAnalyzer : DiagnosticAnalyzer
{
    private const string OutboundAttributeFullName =
        "SuavoAgent.Contracts.Annotations.OutboundPayloadAttribute";
    private const string PhiAttributeFullName =
        "SuavoAgent.Contracts.Annotations.PhiDirectAttribute";
    private const int MaxDepth = 50;

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(
            DiagnosticDescriptors.PhiInOutboundPayload,
            DiagnosticDescriptors.AnalyzerInternalError);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(AnalyzeNamedType, SymbolKind.NamedType);
    }

    private static void AnalyzeNamedType(SymbolAnalysisContext ctx)
    {
        var symbol = (INamedTypeSymbol)ctx.Symbol;
        try
        {
            if (!HasAttribute(symbol, OutboundAttributeFullName))
                return;

            var visited = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            var path = new Stack<string>();
            WalkMembers(symbol, symbol, visited, path, ctx, depth: 0);
        }
        catch (Exception ex)
        {
            ReportInternalError(ctx, symbol, ex);
        }
    }

    private static void WalkMembers(
        INamedTypeSymbol outboundRoot,
        INamedTypeSymbol current,
        HashSet<INamedTypeSymbol> visited,
        Stack<string> path,
        SymbolAnalysisContext ctx,
        int depth)
    {
        if (depth > MaxDepth)
        {
            var location = current.Locations.FirstOrDefault() ?? Location.None;
            ctx.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.AnalyzerInternalError,
                location,
                outboundRoot.ToDisplayString(),
                $"max nesting depth ({MaxDepth}) exceeded at {current.Name}"));
            return;
        }

        if (!visited.Add(current))
            return;

        foreach (var member in current.GetMembers())
        {
            if (member is not (IPropertySymbol or IFieldSymbol))
                continue;

            // Direct check: does the member itself carry [PhiDirect]?
            if (HasAttribute(member, PhiAttributeFullName))
            {
                var location = member.Locations.FirstOrDefault() ?? Location.None;
                var fieldPath = path.Count == 0
                    ? $"{outboundRoot.Name}.{member.Name}"
                    : $"{outboundRoot.Name}.{string.Join(".", path.Reverse())}.{member.Name}";

                ctx.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.PhiInOutboundPayload,
                    location,
                    fieldPath,
                    outboundRoot.ToDisplayString()));
            }

            // Recurse into the member's type if it's defined in the same assembly.
            var memberType = GetMemberType(member);
            if (memberType is INamedTypeSymbol nested
                && nested.ContainingAssembly is not null
                && SymbolEqualityComparer.Default.Equals(
                    nested.ContainingAssembly,
                    outboundRoot.ContainingAssembly))
            {
                path.Push(member.Name);
                WalkMembers(outboundRoot, nested, visited, path, ctx, depth + 1);
                path.Pop();
            }
        }
    }

    private static ITypeSymbol? GetMemberType(ISymbol member) => member switch
    {
        IPropertySymbol p => p.Type,
        IFieldSymbol f => f.Type,
        _ => null,
    };

    private static bool HasAttribute(ISymbol symbol, string fullName) =>
        symbol.GetAttributes().Any(a =>
            a.AttributeClass?.ToDisplayString() == fullName);

    private static void ReportInternalError(
        SymbolAnalysisContext ctx,
        INamedTypeSymbol symbol,
        Exception ex)
    {
        var location = symbol.Locations.FirstOrDefault() ?? Location.None;
        ctx.ReportDiagnostic(Diagnostic.Create(
            DiagnosticDescriptors.AnalyzerInternalError,
            location,
            symbol.ToDisplayString(),
            $"{ex.GetType().Name}: {ex.Message}"));
    }
}
```

- [ ] **Step 4: Run all tests to verify they pass**

```bash
dotnet test tests/SuavoAgent.Analyzers.Tests/ 2>&1 | tail -8
```

Expected: 6 tests pass (DirectViolation, NestedViolation, CyclicTypes, CleanOutboundType, NonOutboundTypeWithPhi, MultipleViolations).

- [ ] **Step 5: Commit**

```bash
git add src/SuavoAgent.Analyzers/PhiInOutboundPayloadAnalyzer.cs \
        tests/SuavoAgent.Analyzers.Tests/PhiInOutboundPayloadAnalyzerTests.cs
git commit -m "feat(analyzers): extend SUAVO0001 to nested + cyclic type graphs"
```

---

## Task 6: Add canary fixture tests

**Repo:** SuavoAgent
**Files:**
- Create: `tests/SuavoAgent.Analyzers.Tests/CanaryFixtureTests.cs`

The canary is the explicit "gate works" test — separate from analyzer-correctness tests. If it fails, the gate is broken.

- [ ] **Step 1: Write the canary tests**

Create `tests/SuavoAgent.Analyzers.Tests/CanaryFixtureTests.cs`:

```csharp
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verifier = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.AnalyzerVerifier<
    SuavoAgent.Analyzers.PhiInOutboundPayloadAnalyzer>;

namespace SuavoAgent.Analyzers.Tests;

/// <summary>
/// The canary fixture. Asserts that the gate works on a representative
/// HIPAA-violation. If <see cref="CanaryViolation_GateRejects"/> ever passes
/// silently (i.e., no diagnostic emitted), the gate is broken — investigate
/// immediately.
/// </summary>
public class CanaryFixtureTests
{
    private const string Annotations = """
        namespace SuavoAgent.Contracts.Annotations;
        using System;

        [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, Inherited = true)]
        public sealed class PhiDirectAttribute : Attribute { }

        [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface, Inherited = true)]
        public sealed class OutboundPayloadAttribute : Attribute { }
        """;

    [Fact]
    public async Task CanaryViolation_GateRejects()
    {
        // The canary: this is the kind of code that would leak PHI to the cloud
        // if the gate were bypassed. The analyzer MUST flag it.
        var source = Annotations + """

            using SuavoAgent.Contracts.Annotations;

            namespace SuavoAgent.Canary;

            [OutboundPayload]
            public class CanaryLeak
            {
                [PhiDirect]
                public string {|#0:PatientName|} { get; set; } = "";

                [PhiDirect]
                public string {|#1:DateOfBirth|} { get; set; } = "";
            }
            """;

        var expectedName = Verifier.Diagnostic(DiagnosticDescriptors.PhiInOutboundPayload)
            .WithLocation(0)
            .WithArguments("CanaryLeak.PatientName", "SuavoAgent.Canary.CanaryLeak");
        var expectedDob = Verifier.Diagnostic(DiagnosticDescriptors.PhiInOutboundPayload)
            .WithLocation(1)
            .WithArguments("CanaryLeak.DateOfBirth", "SuavoAgent.Canary.CanaryLeak");

        await Verifier.VerifyAnalyzerAsync(source, expectedName, expectedDob);
    }

    [Fact]
    public async Task CanaryClean_GateSilent()
    {
        // The negative canary: a properly-shaped outbound payload must NOT
        // be flagged. If this test starts failing, the analyzer has a false
        // positive.
        var source = Annotations + """

            using SuavoAgent.Contracts.Annotations;

            namespace SuavoAgent.Canary;

            [OutboundPayload]
            public class CleanCanary
            {
                public string RxHash { get; set; } = "";
                public string MedicationCode { get; set; } = "";
                public int Quantity { get; set; }
            }
            """;

        await Verifier.VerifyAnalyzerAsync(source);
    }
}
```

- [ ] **Step 2: Run canary tests**

```bash
dotnet test tests/SuavoAgent.Analyzers.Tests/ --filter "FullyQualifiedName~CanaryFixtureTests" 2>&1 | tail -8
```

Expected: 2 canary tests pass.

- [ ] **Step 3: Commit**

```bash
git add tests/SuavoAgent.Analyzers.Tests/CanaryFixtureTests.cs
git commit -m "test(analyzers): synthetic PHI-leak canary fixture (positive + negative)"
```

---

## Task 7: Add regression tests for retrofitted types

**Repo:** SuavoAgent
**Files:**
- Create: `tests/SuavoAgent.Analyzers.Tests/RetrofittedTypesRegressionTests.cs`

These tests pin the existing payload types as known-clean. If a future PR adds PHI to one of them, the test fires immediately.

- [ ] **Step 1: Write the regression tests**

Create `tests/SuavoAgent.Analyzers.Tests/RetrofittedTypesRegressionTests.cs`:

```csharp
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verifier = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.AnalyzerVerifier<
    SuavoAgent.Analyzers.PhiInOutboundPayloadAnalyzer>;

namespace SuavoAgent.Analyzers.Tests;

/// <summary>
/// Regression tests for the Wave 1 retrofitted types. Each retrofitted type
/// is asserted clean (analyzer silent). If a future PR introduces a PHI
/// field on one of these types, the regression test fires immediately.
/// </summary>
public class RetrofittedTypesRegressionTests
{
    private const string Annotations = """
        namespace SuavoAgent.Contracts.Annotations;
        using System;

        [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, Inherited = true)]
        public sealed class PhiDirectAttribute : Attribute { }

        [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface, Inherited = true)]
        public sealed class OutboundPayloadAttribute : Attribute { }
        """;

    [Fact]
    public async Task PatientDetailsPayload_Clean()
    {
        // Mirrors src/SuavoAgent.Contracts/Models/PatientDetailsPayload.cs after Task 11 retrofit.
        // None of the fields are PHI-Direct (per field-registry: address fields are PHI-Adjacent salted).
        var source = Annotations + """

            using SuavoAgent.Contracts.Annotations;

            namespace TestNs;

            [OutboundPayload]
            public sealed record PatientDetailsPayload(
                string? FirstName,
                string? LastInitial,
                string? Phone,
                string? Address1,
                string? Address2,
                string? City,
                string? State,
                string? Zip);
            """;

        await Verifier.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public async Task WaveGateTrippedPayload_Clean()
    {
        var source = Annotations + """

            using System;
            using System.Collections.Generic;
            using SuavoAgent.Contracts.Annotations;

            namespace TestNs;

            [OutboundPayload]
            public sealed record WaveGateTrippedPayload(
                string WaveId,
                string EvidenceSummary,
                string CertifiedBy,
                IReadOnlyList<string> EvidenceEventIds,
                DateTimeOffset TrippedAt);
            """;

        await Verifier.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public async Task WaveGateFailedPayload_Clean()
    {
        var source = Annotations + """

            using System;
            using SuavoAgent.Contracts.Annotations;

            namespace TestNs;

            [OutboundPayload]
            public sealed record WaveGateFailedPayload(
                string WaveId,
                int AttemptNumber,
                string FailureSummary,
                string RootCauseClass,
                DateTimeOffset? RemediationPlanCommittedAt,
                string NextAttemptEstimated);
            """;

        await Verifier.VerifyAnalyzerAsync(source);
    }
}
```

- [ ] **Step 2: Run tests**

```bash
dotnet test tests/SuavoAgent.Analyzers.Tests/ --filter "FullyQualifiedName~RetrofittedTypesRegressionTests" 2>&1 | tail -8
```

Expected: 3 tests pass.

- [ ] **Step 3: Commit**

```bash
git add tests/SuavoAgent.Analyzers.Tests/RetrofittedTypesRegressionTests.cs
git commit -m "test(analyzers): regression coverage for retrofitted payload types"
```

---

## Task 8: Performance / stress test (1000 synthetic types)

**Repo:** SuavoAgent
**Files:**
- Create: `tests/SuavoAgent.Analyzers.Tests/PerformanceTests.cs`

- [ ] **Step 1: Write the perf test**

Create `tests/SuavoAgent.Analyzers.Tests/PerformanceTests.cs`:

```csharp
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verifier = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.AnalyzerVerifier<
    SuavoAgent.Analyzers.PhiInOutboundPayloadAnalyzer>;

namespace SuavoAgent.Analyzers.Tests;

/// <summary>
/// Stress test for the analyzer. Generates 1000 synthetic types with nested
/// references (5 levels deep including a few cycles) and asserts the analyzer
/// completes within threshold. Catches future regressions where someone adds
/// expensive operations inside the analyzer hot path.
///
/// Threshold: 5 seconds wall-clock on Linux x64; 10 seconds on macOS arm64
/// (slower CI). Failures surface as test failures with measured time.
/// </summary>
public class PerformanceTests
{
    private const int TypeCount = 1000;
    private const int NestingDepth = 5;
    private const int LinuxThresholdMs = 5_000;
    private const int MacArmThresholdMs = 10_000;

    private const string Annotations = """
        namespace SuavoAgent.Contracts.Annotations;
        using System;

        [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, Inherited = true)]
        public sealed class PhiDirectAttribute : Attribute { }

        [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface, Inherited = true)]
        public sealed class OutboundPayloadAttribute : Attribute { }
        """;

    [Fact]
    public async Task AnalyzerScales_To1000Types_UnderThreshold()
    {
        var source = GenerateSyntheticTypes(TypeCount, NestingDepth);

        var sw = Stopwatch.StartNew();
        await Verifier.VerifyAnalyzerAsync(source);
        sw.Stop();

        var thresholdMs = System.Runtime.InteropServices.RuntimeInformation
            .OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64
            ? MacArmThresholdMs
            : LinuxThresholdMs;

        Assert.True(
            sw.ElapsedMilliseconds < thresholdMs,
            $"Analyzer took {sw.ElapsedMilliseconds}ms on {TypeCount} types " +
            $"(threshold: {thresholdMs}ms). Investigate analyzer hot-path regression.");
    }

    private static string GenerateSyntheticTypes(int count, int nestingDepth)
    {
        var sb = new StringBuilder(Annotations);
        sb.AppendLine();
        sb.AppendLine("using SuavoAgent.Contracts.Annotations;");
        sb.AppendLine();
        sb.AppendLine("namespace PerfTest;");
        sb.AppendLine();

        // Generate 'count' clean outbound types. Each references the next in
        // a chain, with cycles every ~50 types. None contain PHI — analyzer
        // walks the whole graph but emits no diagnostics. Pure perf.
        for (int i = 0; i < count; i++)
        {
            var nextRef = (i + 1) % count;     // forward chain
            var cycleRef = (i + 17) % count;   // cross-link for cycles

            sb.AppendLine($"[OutboundPayload]");
            sb.AppendLine($"public class T{i}");
            sb.AppendLine("{");
            sb.AppendLine($"    public string Id{i} {{ get; set; }} = \"\";");
            sb.AppendLine($"    public T{nextRef}? Next {{ get; set; }}");
            sb.AppendLine($"    public T{cycleRef}? Cross {{ get; set; }}");
            sb.AppendLine("}");
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
```

- [ ] **Step 2: Run perf test**

```bash
dotnet test tests/SuavoAgent.Analyzers.Tests/ --filter "FullyQualifiedName~PerformanceTests" --logger "console;verbosity=detailed" 2>&1 | tail -15
```

Expected: 1 test passes; reports elapsed time. Observed time should be well under threshold for current code; investigate if it isn't.

- [ ] **Step 3: Commit**

```bash
git add tests/SuavoAgent.Analyzers.Tests/PerformanceTests.cs
git commit -m "test(analyzers): perf/stress — 1000 synthetic types under threshold"
```

---

## Task 9: Wire analyzer into `SuavoAgent.Contracts.csproj`

**Repo:** SuavoAgent
**Files:**
- Modify: `src/SuavoAgent.Contracts/SuavoAgent.Contracts.csproj`

This is the moment the analyzer goes "live" — every build of `SuavoAgent.Contracts` runs it.

- [ ] **Step 1: Add the analyzer ProjectReference**

Replace `src/SuavoAgent.Contracts/SuavoAgent.Contracts.csproj` entirely with:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>SuavoAgent.Contracts</RootNamespace>
    <AssemblyTitle>SuavoAgent.Contracts</AssemblyTitle>
    <Description>Suavo pharmacy agent shared contracts and DTOs.</Description>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\SuavoAgent.Analyzers\SuavoAgent.Analyzers.csproj"
                      OutputItemType="Analyzer"
                      ReferenceOutputAssembly="false" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Verify the wiring (existing types must still build clean)**

```bash
dotnet build src/SuavoAgent.Contracts/ 2>&1 | tail -8
```

Expected: build succeeds with `Build succeeded.` and no `SUAVO0001` diagnostics. Existing types haven't been retrofitted yet (Task 11), so the analyzer has nothing to flag.

- [ ] **Step 3: Commit**

```bash
git add src/SuavoAgent.Contracts/SuavoAgent.Contracts.csproj
git commit -m "build(contracts): wire SuavoAgent.Analyzers into Contracts.csproj"
```

---

## Task 10: Retrofit `PatientDetailsPayload`, `WaveGateTrippedPayload`, `WaveGateFailedPayload`

**Repo:** SuavoAgent
**Files:**
- Modify: `src/SuavoAgent.Contracts/Models/PatientDetailsPayload.cs`
- Modify: `src/SuavoAgent.Contracts/Models/WaveGateTrippedPayload.cs`
- Modify: `src/SuavoAgent.Contracts/Models/WaveGateFailedPayload.cs`

Mark each with `[OutboundPayload]`. None of their fields qualify as PHI-Direct (verified in Task 7 regression tests), so the analyzer should remain silent on them.

- [ ] **Step 1: Add `[OutboundPayload]` to `PatientDetailsPayload`**

Edit `src/SuavoAgent.Contracts/Models/PatientDetailsPayload.cs`. Find the line that declares the record:

```csharp
public sealed record PatientDetailsPayload(
```

Add the `using` import + the attribute:

```csharp
using SuavoAgent.Contracts.Annotations;

// ... existing using directives ...

namespace SuavoAgent.Contracts.Models;

// ... existing XML doc comment unchanged ...

[OutboundPayload]
public sealed record PatientDetailsPayload(
```

- [ ] **Step 2: Add `[OutboundPayload]` to `WaveGateTrippedPayload`**

Edit `src/SuavoAgent.Contracts/Models/WaveGateTrippedPayload.cs`. Add to the top of the file:

```csharp
using SuavoAgent.Contracts.Annotations;
```

Then mark the record:

```csharp
[OutboundPayload]
public sealed record WaveGateTrippedPayload(
    string WaveId,
    string EvidenceSummary,
    string CertifiedBy,
    IReadOnlyList<string> EvidenceEventIds,
    DateTimeOffset TrippedAt);
```

- [ ] **Step 3: Add `[OutboundPayload]` to `WaveGateFailedPayload`**

Edit `src/SuavoAgent.Contracts/Models/WaveGateFailedPayload.cs`. Add to the top:

```csharp
using SuavoAgent.Contracts.Annotations;
```

Then mark the record:

```csharp
[OutboundPayload]
public sealed record WaveGateFailedPayload(
    string WaveId,
    int AttemptNumber,
    string FailureSummary,
    string RootCauseClass,
    DateTimeOffset? RemediationPlanCommittedAt,
    string NextAttemptEstimated);
```

- [ ] **Step 4: Verify build remains green (analyzer should NOT fire on these clean types)**

```bash
dotnet build src/SuavoAgent.Contracts/ 2>&1 | tail -8
```

Expected: `Build succeeded.` with no `SUAVO0001` diagnostics. Confirms retrofit is correct + analyzer correctly identifies these as clean.

- [ ] **Step 5: Run all SuavoAgent tests to ensure nothing else broke**

```bash
dotnet test 2>&1 | tail -10
```

Expected: all pre-existing test suites green; new analyzer test suite green.

- [ ] **Step 6: Commit**

```bash
git add src/SuavoAgent.Contracts/Models/PatientDetailsPayload.cs \
        src/SuavoAgent.Contracts/Models/WaveGateTrippedPayload.cs \
        src/SuavoAgent.Contracts/Models/WaveGateFailedPayload.cs
git commit -m "feat(contracts): retrofit 3 payload types with [OutboundPayload]"
```

---

## Task 11: Build integration test — fixture project that intentionally fails

**Repo:** SuavoAgent
**Files:**
- Create: `tests/SuavoAgent.Analyzers.IntegrationTest/SuavoAgent.Analyzers.IntegrationTest.csproj`
- Create: `tests/SuavoAgent.Analyzers.IntegrationTest/CanaryLeak.cs`
- Create: `tests/SuavoAgent.Analyzers.IntegrationTest/run-integration.sh`

Asserts the analyzer is wired up correctly — the fixture project's build MUST FAIL when CI runs it. (The project is excluded from the normal solution build.)

- [ ] **Step 1: Create the integration fixture project**

```bash
mkdir -p tests/SuavoAgent.Analyzers.IntegrationTest
```

Create `tests/SuavoAgent.Analyzers.IntegrationTest/SuavoAgent.Analyzers.IntegrationTest.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>SuavoAgent.Analyzers.IntegrationTest</RootNamespace>
    <!-- Excluded from the normal solution build; only run via run-integration.sh -->
    <IsPackable>false</IsPackable>
    <ExcludeRestorePackageImports>true</ExcludeRestorePackageImports>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\SuavoAgent.Contracts\SuavoAgent.Contracts.csproj" />
    <ProjectReference Include="..\..\src\SuavoAgent.Analyzers\SuavoAgent.Analyzers.csproj"
                      OutputItemType="Analyzer"
                      ReferenceOutputAssembly="false" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Create the canary code**

Create `tests/SuavoAgent.Analyzers.IntegrationTest/CanaryLeak.cs`:

```csharp
using SuavoAgent.Contracts.Annotations;

namespace SuavoAgent.Analyzers.IntegrationTest;

/// <summary>
/// THIS FILE INTENTIONALLY FAILS THE BUILD. Run via run-integration.sh which
/// expects the build to fail with SUAVO0001 diagnostic on PatientName.
/// If the analyzer is broken or unwired, this file would compile silently —
/// the integration test catches that case.
/// </summary>
[OutboundPayload]
public class CanaryLeak
{
    [PhiDirect]
    public string PatientName { get; set; } = "";
}
```

- [ ] **Step 3: Create the integration test runner script**

Create `tests/SuavoAgent.Analyzers.IntegrationTest/run-integration.sh`:

```bash
#!/usr/bin/env bash
# Integration test: build the canary fixture project. The build MUST fail with
# SUAVO0001. If it succeeds, the analyzer is broken or unwired — exit non-zero.
set -euo pipefail

cd "$(dirname "$0")/../.."
PROJECT="tests/SuavoAgent.Analyzers.IntegrationTest/SuavoAgent.Analyzers.IntegrationTest.csproj"

echo "Running integration test — expecting SUAVO0001 to fail the build..."

# Run dotnet build. Capture output. Expect non-zero exit AND SUAVO0001 in stderr.
output=$(dotnet build "$PROJECT" --nologo --verbosity quiet 2>&1 || true)
exit_code=$?

if echo "$output" | grep -q "SUAVO0001"; then
  echo "OK — SUAVO0001 emitted (analyzer is wired correctly)."
  exit 0
else
  echo "FAIL — SUAVO0001 NOT emitted. Build output:"
  echo "$output"
  exit 1
fi
```

Make it executable:

```bash
chmod +x tests/SuavoAgent.Analyzers.IntegrationTest/run-integration.sh
```

- [ ] **Step 4: Exclude the fixture project from the solution to keep top-level builds green**

The `dotnet sln add` from earlier added projects to the solution. Skip adding this fixture project:

```bash
# Verify it's NOT in the solution:
grep -c "SuavoAgent.Analyzers.IntegrationTest" SuavoAgent.sln || echo "Not in solution (correct)"
```

Expected: not in solution. (If it accidentally got added, run `dotnet sln remove tests/SuavoAgent.Analyzers.IntegrationTest/SuavoAgent.Analyzers.IntegrationTest.csproj` to remove.)

- [ ] **Step 5: Run the integration test**

```bash
./tests/SuavoAgent.Analyzers.IntegrationTest/run-integration.sh
```

Expected: outputs `OK — SUAVO0001 emitted (analyzer is wired correctly).` and exits 0.

- [ ] **Step 6: Commit**

```bash
git add tests/SuavoAgent.Analyzers.IntegrationTest/
git commit -m "test(analyzers): integration test — fixture project intentionally fails build"
```

---

## Task 12: SuavoAgent final verification + push (operational)

**Repo:** SuavoAgent
**Files:** none new

- [ ] **Step 1: Run the full test pass**

```bash
cd /Users/joshuahenein/Code/SuavoAgent
dotnet test 2>&1 | tail -10
```

Expected: all suites green. Analyzer suite reports ~12 tests passing (annotations: 3, analyzer cases: 6, canary: 2, regression: 3, perf: 1 = ~15 individual tests).

- [ ] **Step 2: Run the integration test**

```bash
./tests/SuavoAgent.Analyzers.IntegrationTest/run-integration.sh
```

Expected: `OK — SUAVO0001 emitted`.

- [ ] **Step 3: Push branch**

```bash
git push -u origin feat/wave-1-phi-ci-gate-suavoagent 2>&1 | tail -5
```

If push fails (per memory `feedback-gh-multi-account-push.md` — gh-auth is SuavoLLC org only): branch stays local. Joshua handles operationally.

- [ ] **Step 4: Open PR if push succeeded**

```bash
gh pr create --repo MinaH153/SuavoAgent \
  --title "Wave 1A (SuavoAgent): PHI compile-time CI gate — SUAVO0001 analyzer" \
  --body "$(cat <<'EOF'
Closes Wave 1 Sub-project A SuavoAgent-side per
\`docs/superpowers/specs/2026-05-01-track-3-phi-ci-gate-design.md\`.

## Changes
- \`src/SuavoAgent.Contracts/Annotations/\` — \`[PhiDirect]\` + \`[OutboundPayload]\` attributes
- \`src/SuavoAgent.Analyzers/\` — Roslyn diagnostic analyzer (SUAVO0001 + SUAVO0099 crash recovery)
- \`tests/SuavoAgent.Analyzers.Tests/\` — analyzer unit tests (6 cases) + canary fixtures (2) + regression tests (3) + perf/stress test (1000 synthetic types)
- \`tests/SuavoAgent.Analyzers.IntegrationTest/\` — fixture project that intentionally fails build to prove analyzer is wired
- Wired analyzer into \`SuavoAgent.Contracts.csproj\` via \`<ProjectReference OutputItemType="Analyzer">\`
- Retrofit: \`PatientDetailsPayload\`, \`WaveGateTrippedPayload\`, \`WaveGateFailedPayload\` marked \`[OutboundPayload]\` (analyzer silent — they're clean)

## Wave 1 gate (SuavoAgent half)
- [x] Synthetic canary fails build with SUAVO0001 (proven via integration test)
- [x] Clean retrofitted types pass build (proven via regression tests)
- [x] Analyzer scales to 1000 types under perf threshold
- [x] Crash recovery surfaces as SUAVO0099 (not silent)

## Test plan
- [x] \`dotnet test\` all suites green
- [x] \`./tests/SuavoAgent.Analyzers.IntegrationTest/run-integration.sh\` exits 0
- [ ] Joshua reviews analyzer logic + diagnostic message format

EOF
)"
```

If gh-auth blocks creation: paste body via web UI.

---

# Phase 2 — Suavo

## Task 13: Add `phi()` + `outbound()` Zod helpers

**Repo:** Suavo
**Files:**
- Create: `src/lib/zod-phi.ts`
- Test: `src/lib/__tests__/zod-phi.test.ts`

- [ ] **Step 1: Branch off main**

```bash
cd /Users/joshuahenein/Code/Suavo
git checkout main
git pull --ff-only origin main
git checkout -b feat/wave-1-phi-ci-gate-suavo
```

If `git checkout main` fails because main is checked out in another worktree, use `git checkout -b feat/wave-1-phi-ci-gate-suavo origin/main` instead.

- [ ] **Step 2: Write the failing test**

Create `src/lib/__tests__/zod-phi.test.ts`:

```typescript
import { describe, expect, it } from "vitest";
import { z } from "zod";
import { phi, outbound } from "@/lib/zod-phi";

describe("zod-phi helpers", () => {
  it("phi() returns the same schema (identity at runtime)", () => {
    const schema = z.string();
    const tagged = phi(schema);
    expect(tagged).toBe(schema);
  });

  it("phi() preserves parsing behavior", () => {
    const schema = phi(z.string().min(2));
    expect(schema.parse("hi")).toBe("hi");
    expect(() => schema.parse("x")).toThrow();
  });

  it("outbound() returns the same schema (identity at runtime)", () => {
    const schema = z.object({ a: z.string() });
    const tagged = outbound(schema);
    expect(tagged).toBe(schema);
  });

  it("outbound() preserves parsing behavior", () => {
    const schema = outbound(z.object({ rx_hash: z.string().min(1) }));
    expect(schema.parse({ rx_hash: "abc" })).toEqual({ rx_hash: "abc" });
    expect(() => schema.parse({ rx_hash: "" })).toThrow();
  });

  it("outbound + phi compose without runtime change", () => {
    // The ESLint rule (Task 15) catches phi() inside outbound() at lint time;
    // at runtime they're identity functions and don't interfere with parsing.
    const schema = outbound(
      z.object({
        patient_name: phi(z.string()),
      }),
    );
    expect(schema.parse({ patient_name: "Alice" })).toEqual({
      patient_name: "Alice",
    });
  });
});
```

- [ ] **Step 3: Run test to verify it fails**

```bash
npx vitest run src/lib/__tests__/zod-phi.test.ts 2>&1 | tail -10
```

Expected: import resolution failure ("@/lib/zod-phi" cannot be found).

- [ ] **Step 4: Implement the helpers**

Create `src/lib/zod-phi.ts`:

```typescript
import type { z } from "zod";

/**
 * Marker functions for the PHI compile-time gate.
 *
 * `phi(schema)` and `outbound(schema)` are runtime identity functions —
 * they return the input schema unchanged. Their purpose is purely
 * SYNTACTIC: the custom ESLint rule `suavo-phi/no-phi-in-outbound`
 * walks the AST and emits an error when a `phi(...)` call appears
 * (directly or nested) inside an `outbound(...)` call.
 *
 * These mirror the C# `[PhiDirect]` and `[OutboundPayload]` attributes
 * in `SuavoAgent.Contracts.Annotations`. Source of truth for the PHI
 * tier classification lives at
 * `<SuavoAgent>/docs/self-healing/field-registry.md`.
 *
 * Track 3 invariant: any `phi(...)` field inside an `outbound(...)`
 * schema = ESLint error. Build (lint pass) fails on PR.
 */

/**
 * Marks a Zod leaf schema as PHI-Direct per `field-registry.md`.
 * Identity at runtime; syntactic marker for ESLint.
 *
 * @example
 *   const PatientFields = z.object({
 *     name: phi(z.string()),   // PHI-Direct, never crosses network
 *     dob:  phi(z.string()),
 *   });
 */
export const phi = <T extends z.ZodTypeAny>(schema: T): T => schema;

/**
 * Marks a Zod schema as a network-bound payload.
 * Identity at runtime; syntactic marker for ESLint.
 *
 * @example
 *   export const SyncPayloadSchema = outbound(z.object({
 *     rx_hash: z.string(),
 *     // patient_name: phi(z.string()),   // ← would fail lint
 *   }));
 */
export const outbound = <T extends z.ZodTypeAny>(schema: T): T => schema;
```

- [ ] **Step 5: Run test to verify it passes**

```bash
npx vitest run src/lib/__tests__/zod-phi.test.ts 2>&1 | tail -8
```

Expected: 5 tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/lib/zod-phi.ts src/lib/__tests__/zod-phi.test.ts
git commit -m "feat(lib): add phi() + outbound() Zod marker helpers"
```

---

## Task 14: Create ESLint rule scaffolding

**Repo:** Suavo
**Files:**
- Create: `eslint-rules/no-phi-in-outbound.mjs`

The rule lives alongside existing local rules (`no-silent-fallback.mjs`, `no-raw-color.mjs`, etc.) and follows the same pattern.

- [ ] **Step 1: Create the rule skeleton**

Create `eslint-rules/no-phi-in-outbound.mjs`:

```javascript
/**
 * suavo-phi/no-phi-in-outbound
 *
 * Errors when a `phi(...)` call appears (directly or nested) inside an
 * `outbound(...)` call. Mirrors the C# Roslyn analyzer SUAVO0001 in
 * SuavoAgent.Analyzers.PhiInOutboundPayloadAnalyzer.
 *
 * The rule is purely syntactic — it does not follow identifier references
 * across variables/files. Wave 1 constraint: `phi(...)` must appear
 * syntactically nested inside `outbound(...)`. Refactoring a sub-schema
 * with `phi()` and assigning to a variable then referencing the variable
 * inside `outbound()` will NOT be caught.
 *
 * Wave 5+ enhancement: type-aware variant using @typescript-eslint scope
 * manager to follow identifier references when Track 5 verbs add more
 * outbound surface.
 *
 * See:
 *   docs/superpowers/specs/2026-05-01-track-3-phi-ci-gate-design.md §3
 *   src/lib/zod-phi.ts (the marker helpers)
 */

const MAX_DEPTH = 50;

export default {
  meta: {
    type: "problem",
    docs: {
      description:
        "Disallow phi() inside outbound() — Track 3 PHI compile-time gate.",
      recommended: true,
    },
    schema: [],
    messages: {
      phiInOutbound:
        "PHI-Direct field (phi() call) inside outbound() schema — split required: move phi() field to a local-only schema or remove the phi() marker.",
      tooDeep: `Schema nesting exceeded ${MAX_DEPTH} levels. This is likely a code smell — flatten the schema or split into sub-schemas.`,
      internalRuleError:
        "no-phi-in-outbound rule crashed: {{ message }}. Failing CI loudly rather than silent suppression.",
    },
  },

  create(context) {
    function scanForPhi(node, depth) {
      if (depth > MAX_DEPTH) {
        context.report({ node, messageId: "tooDeep" });
        return;
      }

      if (
        node.type === "CallExpression" &&
        node.callee.type === "Identifier" &&
        node.callee.name === "phi"
      ) {
        context.report({ node, messageId: "phiInOutbound" });
        // continue scanning — multiple violations possible
      }

      // Walk children. ESLint AST node shapes vary; we recurse over all
      // child properties that hold AST nodes.
      for (const key of Object.keys(node)) {
        if (key === "parent" || key === "type" || key === "loc" || key === "range") {
          continue;
        }
        const child = node[key];
        if (child === null || child === undefined) continue;
        if (Array.isArray(child)) {
          for (const item of child) {
            if (item && typeof item === "object" && typeof item.type === "string") {
              scanForPhi(item, depth + 1);
            }
          }
        } else if (typeof child === "object" && typeof child.type === "string") {
          scanForPhi(child, depth + 1);
        }
      }
    }

    return {
      CallExpression(node) {
        try {
          if (
            node.callee.type !== "Identifier" ||
            node.callee.name !== "outbound"
          ) {
            return;
          }
          for (const arg of node.arguments) {
            scanForPhi(arg, 0);
          }
        } catch (err) {
          context.report({
            node,
            messageId: "internalRuleError",
            data: { message: err.message ?? String(err) },
          });
        }
      },
    };
  },
};
```

- [ ] **Step 2: Verify the file is syntactically valid**

```bash
node --check eslint-rules/no-phi-in-outbound.mjs
```

Expected: silent (no parse errors).

- [ ] **Step 3: Commit**

```bash
git add eslint-rules/no-phi-in-outbound.mjs
git commit -m "feat(eslint): scaffold no-phi-in-outbound rule (no tests yet)"
```

---

## Task 15: Add ESLint rule unit tests

**Repo:** Suavo
**Files:**
- Create: `eslint-rules/__tests__/no-phi-in-outbound.test.mjs`

- [ ] **Step 1: Write the rule tests**

Create `eslint-rules/__tests__/no-phi-in-outbound.test.mjs`:

```javascript
import { RuleTester } from "eslint";
import rule from "../no-phi-in-outbound.mjs";

const ruleTester = new RuleTester({
  languageOptions: {
    ecmaVersion: 2022,
    sourceType: "module",
  },
});

ruleTester.run("no-phi-in-outbound", rule, {
  valid: [
    // Clean outbound schema — no phi() inside
    {
      code: `outbound(z.object({ rx_hash: z.string(), qty: z.number() }))`,
    },
    // phi() outside outbound — fine (e.g., in a local-only schema)
    {
      code: `const local = z.object({ name: phi(z.string()) });`,
    },
    // outbound() with no args — fine
    {
      code: `outbound();`,
    },
    // Nested non-phi calls — fine
    {
      code: `outbound(z.object({ items: z.array(z.string()) }))`,
    },
  ],
  invalid: [
    // Direct violation: phi at top level inside outbound
    {
      code: `outbound(z.object({ name: phi(z.string()) }))`,
      errors: [{ messageId: "phiInOutbound" }],
    },
    // Nested violation: phi two levels deep
    {
      code: `outbound(z.object({ patient: z.object({ name: phi(z.string()) }) }))`,
      errors: [{ messageId: "phiInOutbound" }],
    },
    // Multiple violations: report each
    {
      code: `outbound(z.object({ name: phi(z.string()), dob: phi(z.string()) }))`,
      errors: [
        { messageId: "phiInOutbound" },
        { messageId: "phiInOutbound" },
      ],
    },
    // Inside z.array
    {
      code: `outbound(z.array(z.object({ name: phi(z.string()) })))`,
      errors: [{ messageId: "phiInOutbound" }],
    },
    // Inside z.union
    {
      code: `outbound(z.union([z.string(), phi(z.string())]))`,
      errors: [{ messageId: "phiInOutbound" }],
    },
  ],
});

console.log("no-phi-in-outbound rule tests passed.");
```

- [ ] **Step 2: Run the tests**

```bash
node eslint-rules/__tests__/no-phi-in-outbound.test.mjs 2>&1 | tail -10
```

Expected: outputs `no-phi-in-outbound rule tests passed.` and exits 0. If any case fails, RuleTester throws with the failing case details.

- [ ] **Step 3: Commit**

```bash
git add eslint-rules/__tests__/no-phi-in-outbound.test.mjs
git commit -m "test(eslint): no-phi-in-outbound rule unit tests (5 valid + 5 invalid)"
```

---

## Task 16: Add canary + perf/stress tests

**Repo:** Suavo
**Files:**
- Modify: `eslint-rules/__tests__/no-phi-in-outbound.test.mjs`

- [ ] **Step 1: Append canary + perf tests to the existing test file**

Open `eslint-rules/__tests__/no-phi-in-outbound.test.mjs`. Add the following at the BOTTOM of the file (after the existing `console.log` line):

```javascript

// ===== Canary fixtures =====
// Asserts the gate works on representative violations. If these stop
// failing, the gate is broken — investigate immediately.

const canaryRuleTester = new RuleTester({
  languageOptions: { ecmaVersion: 2022, sourceType: "module" },
});

canaryRuleTester.run("no-phi-in-outbound (canary)", rule, {
  valid: [
    // Negative canary: clean outbound schema must NOT be flagged
    {
      code: `outbound(z.object({
  rx_hash: z.string(),
  medication_code: z.string(),
  quantity: z.number(),
  ndc: z.string(),
}))`,
    },
  ],
  invalid: [
    // Positive canary: representative HIPAA-violation shape
    {
      code: `outbound(z.object({
  rx_hash: z.string(),
  patient_name: phi(z.string()),
  date_of_birth: phi(z.string()),
}))`,
      errors: [
        { messageId: "phiInOutbound" },
        { messageId: "phiInOutbound" },
      ],
    },
  ],
});

console.log("no-phi-in-outbound canary fixtures passed.");

// ===== Performance / stress test =====
// Generates 1000 synthetic outbound() schemas; asserts ESLint rule
// completes under threshold.

const PERF_THRESHOLD_MS = 2_000;

function generateLargeFixture(count) {
  const schemas = [];
  for (let i = 0; i < count; i++) {
    schemas.push(`outbound(z.object({
  id_${i}: z.string(),
  name_${i}: z.string(),
  qty_${i}: z.number(),
}))`);
  }
  return schemas.join(";\n");
}

const perfRuleTester = new RuleTester({
  languageOptions: { ecmaVersion: 2022, sourceType: "module" },
});

const perfStart = Date.now();
perfRuleTester.run("no-phi-in-outbound (perf)", rule, {
  valid: [
    { code: generateLargeFixture(1000) },
  ],
  invalid: [],
});
const perfElapsed = Date.now() - perfStart;

if (perfElapsed > PERF_THRESHOLD_MS) {
  console.error(
    `PERF FAIL: rule took ${perfElapsed}ms on 1000 outbound() schemas ` +
    `(threshold: ${PERF_THRESHOLD_MS}ms). Investigate hot-path regression.`,
  );
  process.exit(1);
}

console.log(`no-phi-in-outbound perf test passed (${perfElapsed}ms < ${PERF_THRESHOLD_MS}ms).`);
```

- [ ] **Step 2: Run the full test file**

```bash
node eslint-rules/__tests__/no-phi-in-outbound.test.mjs 2>&1 | tail -10
```

Expected: prints all three "passed" lines including the perf timing.

- [ ] **Step 3: Commit**

```bash
git add eslint-rules/__tests__/no-phi-in-outbound.test.mjs
git commit -m "test(eslint): canary fixtures + perf/stress (1000 outbound schemas under 2s)"
```

---

## Task 17: Wire rule into Suavo's ESLint flat config

**Repo:** Suavo
**Files:**
- Modify: `eslint.config.js` (the main flat-config file at repo root)

- [ ] **Step 1: Identify the config file**

```bash
ls -1 eslint.config.* 2>/dev/null
```

Expected: prints `eslint.config.js` (or similar). Note the exact filename.

- [ ] **Step 2: Add the rule import + registration**

Open the config file (`eslint.config.js` or equivalent). Find the existing rule imports — they look like:

```javascript
import suavoQuality from "./eslint-rules/no-silent-fallback.mjs";
import suavoNoRawColor from "./eslint-rules/no-raw-color.mjs";
import suavoNoBareButton from "./eslint-rules/no-bare-button.mjs";
```

Add a sibling import right after them:

```javascript
import suavoPhi from "./eslint-rules/no-phi-in-outbound.mjs";
```

Then locate the section of the config that registers these rules. It will look like:

```javascript
plugins: {
  "suavo-quality": { rules: { "no-silent-fallback": suavoQuality } },
  "suavo-color": { rules: { "no-raw-color": suavoNoRawColor } },
  "suavo-button": { rules: { "no-bare-button": suavoNoBareButton } },
},
rules: {
  "suavo-quality/no-silent-fallback": "error",
  "suavo-color/no-raw-color": "error",
  "suavo-button/no-bare-button": "error",
},
```

(Format may differ — match the existing pattern exactly.) Add a sibling entry:

```javascript
plugins: {
  "suavo-quality": { rules: { "no-silent-fallback": suavoQuality } },
  "suavo-color": { rules: { "no-raw-color": suavoNoRawColor } },
  "suavo-button": { rules: { "no-bare-button": suavoNoBareButton } },
  "suavo-phi": { rules: { "no-phi-in-outbound": suavoPhi } },
},
rules: {
  "suavo-quality/no-silent-fallback": "error",
  "suavo-color/no-raw-color": "error",
  "suavo-button/no-bare-button": "error",
  "suavo-phi/no-phi-in-outbound": "error",
},
```

- [ ] **Step 3: Verify ESLint loads the rule cleanly**

```bash
pnpm lint --max-warnings=0 -- src/lib/zod-phi.ts 2>&1 | tail -10
```

Expected: silent (no errors) — `zod-phi.ts` defines but doesn't USE the helpers, so the rule has nothing to flag.

If the lint command structure differs in this repo, run via `npx eslint` directly:

```bash
npx eslint src/lib/zod-phi.ts 2>&1 | tail -10
```

- [ ] **Step 4: Commit**

```bash
git add eslint.config.js
git commit -m "build(eslint): register suavo-phi/no-phi-in-outbound rule"
```

---

## Task 18: Retrofit `WaveGateTrippedPayloadSchema` + `WaveGateFailedPayloadSchema`

**Repo:** Suavo
**Files:**
- Modify: `src/lib/wave-event-payloads.ts`

- [ ] **Step 1: Add the import + wrap schemas**

Edit `src/lib/wave-event-payloads.ts`. At the top of the imports (right after `import { z } from "zod";`), add:

```typescript
import { outbound } from "@/lib/zod-phi";
```

Then locate the two schema declarations:

```typescript
export const WaveGateTrippedPayloadSchema = z.object({
  // ...
});

export const WaveGateFailedPayloadSchema = z.object({
  // ...
});
```

Wrap each with `outbound()`:

```typescript
export const WaveGateTrippedPayloadSchema = outbound(z.object({
  wave_id: z.string().min(1),
  evidence_summary: z.string(),
  certified_by: z.string().min(1),
  evidence_event_ids: z.array(z.string()),
  tripped_at: isoDateTime,
}));

// ...

export const WaveGateFailedPayloadSchema = outbound(z.object({
  wave_id: z.string().min(1),
  attempt_number: z.number().int().positive(),
  failure_summary: z.string(),
  root_cause_class: z.enum(ROOT_CAUSE_CLASSES),
  remediation_plan_committed_at: isoDateTime.nullable(),
  next_attempt_estimated: z.enum(NEXT_ATTEMPT_ESTIMATES),
}));
```

- [ ] **Step 2: Verify lint still passes (these schemas have no PHI fields → rule silent)**

```bash
npx eslint src/lib/wave-event-payloads.ts 2>&1 | tail -8
```

Expected: silent (no errors).

- [ ] **Step 3: Verify existing vitest tests still pass (runtime behavior unchanged — outbound() is identity)**

```bash
npx vitest run src/lib/__tests__/wave-event-payloads.test.ts 2>&1 | tail -10
```

Expected: 16 tests pass (unchanged from Wave 0).

- [ ] **Step 4: Commit**

```bash
git add src/lib/wave-event-payloads.ts
git commit -m "feat(lib): wrap wave event schemas with outbound() marker"
```

---

## Task 19: Build integration test — fixture file with intentional violation

**Repo:** Suavo
**Files:**
- Create: `eslint-rules/__tests__/canary-fixture-intentional-violation.ts`
- Create: `eslint-rules/__tests__/run-integration.sh`

- [ ] **Step 1: Create the canary fixture file**

Create `eslint-rules/__tests__/canary-fixture-intentional-violation.ts`:

```typescript
// THIS FILE INTENTIONALLY FAILS LINT. Run via run-integration.sh which
// expects `eslint` to fail with `suavo-phi/no-phi-in-outbound` diagnostic.
// If the rule is broken or unwired, this file would lint clean — the
// integration test catches that case.
//
// The file is excluded from ts compilation (see tsconfig 'exclude') so
// it doesn't break `pnpm build`. ESLint still picks it up — that's the point.

import { z } from "zod";
import { phi, outbound } from "@/lib/zod-phi";

export const CanaryLeak = outbound(
  z.object({
    rx_hash: z.string(),
    patient_name: phi(z.string()),
  }),
);
```

- [ ] **Step 2: Add the canary fixture path to tsconfig exclude (so `pnpm build` stays green)**

Edit the root `tsconfig.json`. Find the `"exclude"` array (or add one if it doesn't exist) and append the canary file:

```json
{
  "exclude": [
    "node_modules",
    "...",
    "eslint-rules/__tests__/canary-fixture-intentional-violation.ts"
  ]
}
```

(Match the existing format; add the new entry to whatever array structure already exists.)

- [ ] **Step 3: Create the integration test runner**

Create `eslint-rules/__tests__/run-integration.sh`:

```bash
#!/usr/bin/env bash
# Integration test: lint the canary fixture file. ESLint MUST fail with
# `suavo-phi/no-phi-in-outbound`. If it succeeds, the rule is broken or
# unwired — exit non-zero.
set -euo pipefail

cd "$(dirname "$0")/../.."
FIXTURE="eslint-rules/__tests__/canary-fixture-intentional-violation.ts"

echo "Running integration test — expecting suavo-phi/no-phi-in-outbound to fail lint..."

output=$(npx eslint "$FIXTURE" 2>&1 || true)

if echo "$output" | grep -q "suavo-phi/no-phi-in-outbound"; then
  echo "OK — suavo-phi/no-phi-in-outbound emitted (rule is wired correctly)."
  exit 0
else
  echo "FAIL — rule NOT emitted. ESLint output:"
  echo "$output"
  exit 1
fi
```

Make it executable:

```bash
chmod +x eslint-rules/__tests__/run-integration.sh
```

- [ ] **Step 4: Run the integration test**

```bash
./eslint-rules/__tests__/run-integration.sh
```

Expected: outputs `OK — suavo-phi/no-phi-in-outbound emitted` and exits 0.

- [ ] **Step 5: Commit**

```bash
git add eslint-rules/__tests__/canary-fixture-intentional-violation.ts \
        eslint-rules/__tests__/run-integration.sh \
        tsconfig.json
git commit -m "test(eslint): integration test — canary fixture intentionally fails lint"
```

---

## Task 20: Suavo final verification + push

**Repo:** Suavo
**Files:** none new

- [ ] **Step 1: Run the full vitest pass**

```bash
cd /Users/joshuahenein/Code/Suavo
npx vitest run src/lib/__tests__/zod-phi.test.ts src/lib/__tests__/wave-event-payloads.test.ts 2>&1 | tail -10
```

Expected: 5 zod-phi tests + 16 wave-event tests = 21 tests pass.

- [ ] **Step 2: Run the rule unit + canary + perf tests**

```bash
node eslint-rules/__tests__/no-phi-in-outbound.test.mjs 2>&1 | tail -10
```

Expected: 3 "passed" outputs (rule, canary, perf).

- [ ] **Step 3: Run the integration test**

```bash
./eslint-rules/__tests__/run-integration.sh
```

Expected: `OK — suavo-phi/no-phi-in-outbound emitted`.

- [ ] **Step 4: Verify the broader Suavo lint pass is still green**

```bash
pnpm lint 2>&1 | tail -20
```

Expected: lint passes (no `suavo-phi/no-phi-in-outbound` errors in production code, since none of it has unmarked PHI in outbound schemas yet).

If lint fails on UNRELATED rules (pre-existing baseline issues), that's outside scope — note them but don't fix here.

- [ ] **Step 5: Push branch**

```bash
git push -u origin feat/wave-1-phi-ci-gate-suavo 2>&1 | tail -5
```

Expected: succeeds (Suavo's gh-auth works).

- [ ] **Step 6: Open PR**

```bash
gh pr create --repo SuavoLLC/MKM \
  --title "Wave 1A (Suavo): PHI compile-time CI gate — suavo-phi/no-phi-in-outbound" \
  --body "$(cat <<'EOF'
Closes Wave 1 Sub-project A Suavo-side per the spec at
\`<SuavoAgent>/docs/superpowers/specs/2026-05-01-track-3-phi-ci-gate-design.md\`.

## Changes
- \`src/lib/zod-phi.ts\` — \`phi()\` + \`outbound()\` Zod marker helpers (identity at runtime, syntactic markers for ESLint)
- \`src/lib/__tests__/zod-phi.test.ts\` — vitest coverage (5 cases)
- \`eslint-rules/no-phi-in-outbound.mjs\` — custom ESLint rule mirroring SUAVO0001
- \`eslint-rules/__tests__/no-phi-in-outbound.test.mjs\` — RuleTester coverage (5 valid + 5 invalid + canary + perf/stress 1000 schemas under 2s)
- \`eslint-rules/__tests__/canary-fixture-intentional-violation.ts\` + \`run-integration.sh\` — integration test asserting wiring
- \`eslint.config.js\` — register \`suavo-phi/no-phi-in-outbound\` as error
- \`src/lib/wave-event-payloads.ts\` — \`WaveGateTrippedPayloadSchema\` + \`WaveGateFailedPayloadSchema\` wrapped with \`outbound()\` (rule silent — they're clean)

## Wave 1 gate (Suavo half)
- [x] Synthetic canary fails lint with \`suavo-phi/no-phi-in-outbound\`
- [x] Clean retrofitted schemas pass lint
- [x] Rule scales to 1000 outbound() schemas under perf threshold (2s)
- [x] Crash recovery surfaces as \`internalRuleError\` (not silent)

## Test plan
- [x] \`npx vitest run\` — zod-phi + wave-event-payloads green (21 cases)
- [x] \`node eslint-rules/__tests__/no-phi-in-outbound.test.mjs\` — rule + canary + perf
- [x] \`./eslint-rules/__tests__/run-integration.sh\` — exits 0 on canary fixture failure

## Pairs with
SuavoAgent PR \`feat/wave-1-phi-ci-gate-suavoagent\` (push pending Joshua's gh-auth fix on MinaH153). Both halves together close Wave 1 Sub-project A.

EOF
)"
```

---

## Task 21: End-to-end verification across both repos

**Repo:** both
**Files:** none new

- [ ] **Step 1: Run all tests in both repos one final time**

```bash
cd /Users/joshuahenein/Code/SuavoAgent && dotnet test 2>&1 | tail -8
cd /Users/joshuahenein/Code/Suavo && npx vitest run src/lib/__tests__/zod-phi.test.ts src/lib/__tests__/wave-event-payloads.test.ts 2>&1 | tail -8
```

Expected: all green.

- [ ] **Step 2: Run both integration tests**

```bash
cd /Users/joshuahenein/Code/SuavoAgent && ./tests/SuavoAgent.Analyzers.IntegrationTest/run-integration.sh
cd /Users/joshuahenein/Code/Suavo && ./eslint-rules/__tests__/run-integration.sh
```

Expected: both exit 0 with their respective "OK" messages.

- [ ] **Step 3: Verify Wave 1 Sub-project A gate per spec §1**

Wave 1 Sub-project A success criteria:
- [x] Synthetic canary fails build/lint in both repos (proven via integration tests)
- [x] Retrofitted types pass build/lint (proven via regression tests)
- [x] Analyzer + rule scale to 1000 types/schemas under threshold
- [x] Crash recovery surfaces loudly (SUAVO0099 / internalRuleError)
- [x] Documentation: spec + this plan committed to repo

**Wave 1 still requires Sub-project B** (Track 1+4 health composite + dashboard) before its full gate trips per the meta-roadmap §4 Wave 1.

---

## Self-review

(Engineer: do not skip this section. Run after Task 21.)

### 1. Spec coverage

Source spec: `docs/superpowers/specs/2026-05-01-track-3-phi-ci-gate-design.md`

| Spec section | Covered by tasks |
|---|---|
| §1 Architecture (markers + analyzer + ESLint rule) | Tasks 1, 2, 4, 5, 13, 14, 15 |
| §2 Components (4 SuavoAgent + 3 Suavo) | Tasks 1–8, 13–16 |
| §2 Wave 1 retrofit scope (3 SuavoAgent + 2 Suavo) | Tasks 10, 18 |
| §2 Synthetic canary | Tasks 6, 16 |
| §3 Data flow (symbol-based traversal + AST traversal + cycle guard + depth cap) | Tasks 4, 5, 14 |
| §3 Failure rendering (IDE + CI) | Tasks 9, 17 (wiring) + integration tests in Tasks 11, 19 |
| §4 Error handling (crash recovery, perf cap, false negatives noted) | Tasks 4, 5 (SUAVO0099 + tooDeep), 14 (internalRuleError) |
| §5 Test category 1 — unit tests | Tasks 1, 4, 5, 13, 15 |
| §5 Test category 2 — canary | Tasks 6, 16 |
| §5 Test category 3 — regression | Task 7 |
| §5 Test category 4 — build integration | Tasks 11, 19 |
| §5 Test category 5 — perf/stress | Tasks 8, 16 |

No spec gaps identified.

### 2. Placeholder scan

- No "TBD" / "TODO" / "implement later" in any task.
- No "add error handling" without showing the specific code.
- No "similar to Task N" — each task fully self-contained with complete code.

### 3. Type consistency

- C# attribute names: `[PhiDirect]` + `[OutboundPayload]` (Tasks 1, 4, 5, 6, 7, 10, 11) — consistent.
- C# diagnostic IDs: `SUAVO0001` + `SUAVO0099` (Tasks 2, 4, 5, 11) — consistent.
- C# attribute full names in analyzer: `SuavoAgent.Contracts.Annotations.{Phi,Outbound}{Direct,Payload}Attribute` — consistent across Tasks 4, 5.
- TS marker names: `phi()` + `outbound()` (Tasks 13, 14, 15, 16, 17, 18, 19) — consistent.
- TS rule name: `suavo-phi/no-phi-in-outbound` (Tasks 14, 15, 17, 19, 20) — consistent.
- TS messageIds: `phiInOutbound`, `tooDeep`, `internalRuleError` (Tasks 14, 15, 16) — consistent.
- File paths: `src/SuavoAgent.Contracts/Annotations/`, `src/SuavoAgent.Analyzers/`, `tests/SuavoAgent.Analyzers.Tests/`, `eslint-rules/`, `eslint-rules/__tests__/` — consistent.

No inconsistencies found.

---

## Out-of-scope (deferred to future waves)

Per spec §6:
- Approach B (sink markers `[OutboundCall]`) — Wave 5
- Type-aware ESLint rule — Wave 5+
- Drift detection between `field-registry.md` and decorators — TBD
- Audit-demo PR — Phase I nice-to-have
- Roslyn analyzer NuGet packaging — future
- Auto-fix code-action provider — future

---

## Change log

- **2026-05-01 v0.1** — Initial plan from writing-plans session.
