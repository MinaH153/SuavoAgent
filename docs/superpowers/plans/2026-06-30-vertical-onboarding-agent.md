# Vertical-Agnostic Onboarding — AGENT (.NET) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The .NET agent honors a server-authoritative, signed `verticalConfig`: it selects a connector (`PioneerRxConnector` vs `NullConnector`) through a new `ISystemConnector`, renders install consent per `complianceMode` (hipaa BA ack vs minimal Terms), **verifies the config signature**, **fails closed to HIPAA** on absent/unknown/invalid config, and **refuses any downgrade** below its last-known-good compliance mode.

**Architecture:** Cloud is the sole authority (other plan). The agent receives `{ verticalConfig, verticalConfigSignature }` at `/api/agent/register`, parses it fail-soft (like `reasoning`), verifies the signature against the **already-embedded OTA update public key** using RFC-8785-canonical JSON + the existing `BinaryDownloader.VerifyChecksumSignature` DER primitive, runs it through a `CompliancePosture` resolver (fail-closed + downgrade-refusal), bakes it into `appsettings.json`, and selects the install connector from it. The runtime stays **strict-by-default** (today's actuation/vision/scrubber gates are unchanged); a `default` install simply has no PMS to watch and minimal consent — we do NOT relax the PHI machinery.

**Tech Stack:** .NET 8, Avalonia (installer GUI), xUnit, `System.Security.Cryptography.ECDsa`, the in-repo `JsonCanonicalizer` (RFC 8785, in `SuavoAgent.Diagnostics`).

## Global Constraints

Every task implicitly includes these (verbatim from the spec's 5 correctness rules):

- **Cloud is sole authority** for `vertical` + `complianceMode`. The agent treats the config **read-only — no env/registry/CLI override path may weaken it.** (`ConfigOverrideStore` must block the compliance paths — see Task 4.)
- **Fail CLOSED to HIPAA** when the config is absent / unparseable / signature-invalid / weaker-than-last-known-good. NEVER fail to `none`. Effective mode on any failure = `hipaa` (scrubbing on, PHI actuation blocked — i.e., today's strict default).
- **Downgrade refusal (TLS-style):** the agent persists last-known-good `complianceMode` to a protected file and refuses any incoming mode strictly weaker than it. Strictness order: `hipaa` > `pci` > `none`. **The REAL floor is the cloud business record** (it authoritatively serves `hipaa` for a pharmacy); the local last-known-good is **in-install-lifetime defense-in-depth** against a mid-lifetime MITM downgrade. Uninstall wipes `%ProgramData%\SuavoAgent` (`ServiceInstaller.cs:~186`) so a reinstall re-derives posture from the cloud — that is correct, because the cloud is authoritative. Do NOT rely on local LKG surviving a reinstall.
- **Enforcement is server-side** (cloud egress gate). The agent's consent/connector selection is UX + local posture; it never relaxes the server gate. Do not weaken the existing `ActuationConfig.SafeDefault()`, `VisionBootstrap` (Enabled=false), `PhiPolicy`, or `AdapterRegistry` PHI-policy invariant.
- **Connectors = agent CODE** behind `ISystemConnector`; **compliance mapping = cloud DATA** (do not hardcode vertical→mode in the agent).
- **Consent = durable, tamper-evident audit record.** `ConsentReceiptData.ToJson` must record `complianceMode` (and, for hipaa, the BAA version) and NEVER omit signed/notarized fields (audit-trail rule).
- **Signing match (DEDICATED key, NOT the OTA key):** the cloud signs `canonicalVerticalJson(config)` (sorted-key, number-free) with `SUAVO_VERTICAL_CONFIG_SIGNING_KEY`, DER, base64, and sends `verticalConfigKeyId` (`'vertical-v1'`). The agent verifies via a small **trust store of embedded `vertical-config-signing-key-<id>.pub.pem` resources** (mirror `RulesetSignatureVerifier`'s embedded-PEM pattern — NOT `BinaryDownloader`'s private OTA key), selecting the key by `verticalConfigKeyId`; unknown keyId → fail (fail-closed). Verify with `DSASignatureFormat.Rfc3279DerSequence` over `JsonCanonicalizer`-canonical bytes of the parsed DTO. The DTO must mirror the cloud `VerticalConfig` **1:1** (same field names, all string/bool) or canonical bytes won't match. **Joshua owes:** commit `vertical-config-signing-key-vertical-v1.pub.pem` as an embedded resource (matching the cloud's `SUAVO_VERTICAL_CONFIG_SIGNING_KEY` private key).
- **Fail-soft parsing, fail-closed posture, NON-bricking rollout:** a malformed `verticalConfig` must NOT throw (catch → null DTO, like `reasoning`). But the install must distinguish three presence states so a corrupt payload isn't mistaken for "no config": **(a) field absent** (legacy cloud) → back-compat PioneerRx + hipaa; **(b) field present but `signature` empty/null** (rollout window, cloud key not live yet) → back-compat PioneerRx + hipaa (fail-closed-to-hipaa, non-bricking); **(c) field present with a non-empty signature that fails verify, OR present+signed but DTO malformed** → tamper → **BLOCK** the install ("couldn't verify your account configuration — contact support"). Only **present + non-empty signature + valid + parsed** honors the config (can be `none`/Null). Carry the raw presence + signature separately to tell (b) from (c).
- **Two verticals only:** `pharmacy` (hipaa/pioneerrx) + `default` (none/none). `pci` recognized by the posture/strictness order but no pci UI ships. **No new PMS connector beyond PioneerRx + Null.**

## File Structure

- **Create** `src/SuavoAgent.Setup/Connectors/ISystemConnector.cs` — interface + `ConnectorProbe`/`ConnectorCapabilities` records.
- **Create** `src/SuavoAgent.Setup/Connectors/PioneerRxConnector.cs` — wraps `PioneerRxDiscovery` + `SqlCredentialDiscovery`.
- **Create** `src/SuavoAgent.Setup/Connectors/NullConnector.cs` — observe-only.
- **Create** `src/SuavoAgent.Setup/Connectors/SystemConnectorFactory.cs` — `Select(systemConnector)` → `ISystemConnector`.
- **Create** `src/SuavoAgent.Setup/VerticalConfigDto.cs` — 1:1 DTO + `IsValid`.
- **Create** `src/SuavoAgent.Setup/VerticalConfigVerifier.cs` — RFC-8785 canonical + DER verify against an **embedded vertical-config trust store** (keyed by `verticalConfigKeyId`).
- **Create** `src/SuavoAgent.Setup/Resources/vertical-config-signing-key-vertical-v1.pub.pem` — embedded public key (Joshua commits the real one; a test-only key is used in unit tests via the internal `VerifyWithKey` overload).
- **Create** `src/SuavoAgent.Core/Compliance/CompliancePosture.cs` — strictness order + `Resolve(incoming, lastKnownGood)` + fail-closed.
- **Create** `src/SuavoAgent.Core/Compliance/LastKnownGoodStore.cs` — read/write protected `compliance-lkg.json`.
- **Modify** `src/SuavoAgent.Setup/InstallTokenService.cs` — parse `verticalConfig` + `verticalConfigSignature` + `verticalConfigKeyId` (the **`/register`** path).
- **Modify** `src/SuavoAgent.Setup/DeviceCodeService.cs` — parse the SAME three fields from the device-token response (currently drops unknown fields, `:~132`).
- **Modify** `src/SuavoAgent.Setup/DeviceCodePairing.cs` — thread the parsed config into `SetupConfig` (currently builds it without vertical data, `:~101`).
- **Modify** `src/SuavoAgent.Setup/SetupConfig.cs` — carry `VerticalConfigDto? VerticalConfig`, `string? VerticalConfigRaw`, `string? VerticalConfigSignature`, `string? VerticalConfigKeyId` (all `[JsonIgnore]`).
- **Modify** `src/SuavoAgent.Setup/Gui/ViewModels/ConnectingViewModel.cs` — thread all of the above into `SetupConfig`.
- **Modify** `src/SuavoAgent.Setup/Gui/Services/InstallOrchestrator.cs` — `BakeVerticalConfig` + connector selection.
- **Modify** `src/SuavoAgent.Setup/ConsoleInstaller.cs` — connector selection (replace direct PioneerRx calls).
- **Modify** `src/SuavoAgent.Setup/Gui/ViewModels/ConsentViewModel.cs` + `Gui/Services/ConsentReceiptData.cs` — complianceMode branch.
- **Modify** `src/SuavoAgent.Core/Cloud/ConfigOverrideStore.cs` — block compliance override paths.
- **Tests** in `tests/SuavoAgent.Setup.Tests/` and `tests/SuavoAgent.Core.Tests/` (xUnit).

**Test commands:**
- Setup: `dotnet test tests/SuavoAgent.Setup.Tests/SuavoAgent.Setup.Tests.csproj`
- Core: `dotnet test tests/SuavoAgent.Core.Tests/SuavoAgent.Core.Tests.csproj`
- Full: `dotnet test SuavoAgent.sln --configuration Release`

---

### Task 1: `ISystemConnector` + PioneerRx/Null implementations + factory

**Files:**
- Create: `src/SuavoAgent.Setup/Connectors/ISystemConnector.cs`, `PioneerRxConnector.cs`, `NullConnector.cs`, `SystemConnectorFactory.cs`
- Test: `tests/SuavoAgent.Setup.Tests/Connectors/SystemConnectorTests.cs`

**Interfaces:**
- Consumes: existing `PioneerRxDiscovery.Discover()` → `DiscoveryResult?` and `SqlCredentialDiscovery.TryAutoDiscover(configPath)` → `SqlCredentials?` (do NOT reimplement — wrap them).
- Produces:
  ```csharp
  public sealed record ConnectorProbe(bool Detected, string? InstallDir, string? ConfigPath, string Message);
  public sealed record ConnectorCapabilities(bool HasPms, string Label, string RedactionProfileId);
  public interface ISystemConnector {
      string Key { get; }                 // "pioneerrx" | "none"
      ConnectorCapabilities Capabilities { get; }
      ConnectorProbe Probe();             // detect the system
      SqlCredentialDiscovery.SqlCredentials? Discover(ConnectorProbe probe); // null for NullConnector
  }
  ```
  `SystemConnectorFactory.Select(string systemConnector)` → `PioneerRxConnector` for `"pioneerrx"`, `NullConnector` for `"none"`, **throws `UnknownConnectorException` for anything else** (caller fails closed — Task 6).

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SuavoAgent.Setup.Tests/Connectors/SystemConnectorTests.cs
using SuavoAgent.Setup.Connectors;
using Xunit;

public class SystemConnectorTests {
    [Fact] public void Factory_selects_pioneerrx() =>
        Assert.Equal("pioneerrx", SystemConnectorFactory.Select("pioneerrx").Key);
    [Fact] public void Factory_selects_null() =>
        Assert.Equal("none", SystemConnectorFactory.Select("none").Key);
    [Fact] public void Factory_throws_on_unknown() =>
        Assert.Throws<UnknownConnectorException>(() => SystemConnectorFactory.Select("redsail"));
    [Fact] public void Null_connector_is_observe_only() {
        var c = new NullConnector();
        Assert.False(c.Capabilities.HasPms);
        Assert.Null(c.Discover(c.Probe()));
        Assert.Equal("none", c.Capabilities.RedactionProfileId);
    }
    [Fact] public void Pioneer_connector_advertises_pms_and_phi_profile() {
        var c = new PioneerRxConnector();
        Assert.True(c.Capabilities.HasPms);
        Assert.Equal("PioneerRx", c.Capabilities.Label);
        Assert.Equal("phi-v1", c.Capabilities.RedactionProfileId);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SuavoAgent.Setup.Tests/SuavoAgent.Setup.Tests.csproj --filter SystemConnectorTests`
Expected: FAIL — types don't exist.

- [ ] **Step 3: Implement**

```csharp
// ISystemConnector.cs
namespace SuavoAgent.Setup.Connectors;
public sealed record ConnectorProbe(bool Detected, string? InstallDir, string? ConfigPath, string Message);
public sealed record ConnectorCapabilities(bool HasPms, string Label, string RedactionProfileId);
public interface ISystemConnector {
    string Key { get; }
    ConnectorCapabilities Capabilities { get; }
    ConnectorProbe Probe();
    SqlCredentialDiscovery.SqlCredentials? Discover(ConnectorProbe probe);
}
public sealed class UnknownConnectorException(string key) : Exception($"Unknown systemConnector '{key}'");
```

```csharp
// PioneerRxConnector.cs
namespace SuavoAgent.Setup.Connectors;
public sealed class PioneerRxConnector : ISystemConnector {
    public string Key => "pioneerrx";
    public ConnectorCapabilities Capabilities => new(HasPms: true, Label: "PioneerRx", RedactionProfileId: "phi-v1");
    public ConnectorProbe Probe() {
        var d = PioneerRxDiscovery.Discover();
        return d is null
            ? new ConnectorProbe(false, null, null, "PioneerRx not found (no-PMS mode)")
            : new ConnectorProbe(true, d.PioneerDir, d.PioneerConfig, "PioneerRx detected");
    }
    public SqlCredentialDiscovery.SqlCredentials? Discover(ConnectorProbe probe) =>
        probe.ConfigPath is null ? null : SqlCredentialDiscovery.TryAutoDiscover(probe.ConfigPath);
}
```

```csharp
// NullConnector.cs
namespace SuavoAgent.Setup.Connectors;
public sealed class NullConnector : ISystemConnector {
    public string Key => "none";
    public ConnectorCapabilities Capabilities => new(HasPms: false, Label: "your system", RedactionProfileId: "none");
    public ConnectorProbe Probe() => new(false, null, null, "Observe-only (no system connector)");
    public SqlCredentialDiscovery.SqlCredentials? Discover(ConnectorProbe probe) => null;
}
```

```csharp
// SystemConnectorFactory.cs
namespace SuavoAgent.Setup.Connectors;
public static class SystemConnectorFactory {
    public static ISystemConnector Select(string systemConnector) => systemConnector switch {
        "pioneerrx" => new PioneerRxConnector(),
        "none" => new NullConnector(),
        _ => throw new UnknownConnectorException(systemConnector),
    };
}
```

- [ ] **Step 4: Run test to verify it passes** — Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/SuavoAgent.Setup/Connectors tests/SuavoAgent.Setup.Tests/Connectors
git commit -m "feat(setup): ISystemConnector with PioneerRx + Null implementations + factory"
```

---

### Task 2: VerticalConfig DTO + parse from /register (fail-soft)

**Files:**
- Create: `src/SuavoAgent.Setup/VerticalConfigDto.cs`
- Modify: `src/SuavoAgent.Setup/InstallTokenService.cs` (`/register` response parse, ~lines 62–86)
- Modify: `src/SuavoAgent.Setup/DeviceCodeService.cs` (device-token response parse, ~line 132 — currently drops unknown fields)
- Modify: `src/SuavoAgent.Setup/DeviceCodePairing.cs` (build `SetupConfig` WITH vertical data, ~line 101)
- Modify: `src/SuavoAgent.Setup/SetupConfig.cs` (add the four `[JsonIgnore]` vertical fields)
- Modify: `src/SuavoAgent.Setup/Gui/ViewModels/ConnectingViewModel.cs` (pass them into `SetupConfig`)
- Test: `tests/SuavoAgent.Setup.Tests/VerticalConfigParseTests.cs`

**Interfaces:**
- Produces: `VerticalConfigDto` mirroring the cloud `VerticalConfig` **1:1** (all `[JsonPropertyName]` lowerCamel matching the cloud keys). BOTH exchange result types (`InstallTokenExchangeResult` from `/register` AND the device-code result from the device-token poll) gain the SAME four carried fields:
  - `string? VerticalConfigRaw` — the raw JSON text of the `verticalConfig` field if present (null = field absent). **Presence is `VerticalConfigRaw != null`** — this is how (b) rollout-window is told from (a) absent and (c) malformed.
  - `VerticalConfigDto? VerticalConfig` — parsed DTO (null = present-but-malformed when `VerticalConfigRaw != null`).
  - `string? VerticalConfigSignature` — base64 DER signature (null/empty = unsigned rollout window).
  - `string? VerticalConfigKeyId` — selects the verifying key.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SuavoAgent.Setup.Tests/VerticalConfigParseTests.cs
using SuavoAgent.Setup;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

public class VerticalConfigParseTests {
    [Fact] public void Parses_full_pharmacy_config() {
        var json = """{"vertical":"pharmacy","complianceMode":"hipaa","systemConnector":"pioneerrx","connectorLabel":"PioneerRx","redactionProfileId":"phi-v1","framing":{"productNoun":"SuavoAgent","systemNoun":"PioneerRx","businessNoun":"pharmacy","idLabel":"NPI"},"compliance":{"baaRequired":true,"consentCopyId":"hipaa-ba-v1"}}""";
        var dto = JsonSerializer.Deserialize<VerticalConfigDto>(json);
        Assert.NotNull(dto);
        Assert.Equal("hipaa", dto!.ComplianceMode);
        Assert.Equal("pioneerrx", dto.SystemConnector);
        Assert.True(dto.IsValid);
    }
    // (a) absent / (b) present+valid / (c) present+malformed — the three presence states Task 6 depends on.
    [Fact] public void Absent_field_yields_null_raw() {
        var data = JsonNode.Parse("""{"apiKey":"k"}""")!.AsObject();
        var p = InstallTokenService.ParseVerticalConfigFromData(data);
        Assert.Null(p.Raw); Assert.Null(p.Dto);
    }
    [Fact] public void Malformed_field_keeps_raw_but_null_dto_never_throws() {
        var data = JsonNode.Parse("""{"verticalConfig":"not-an-object","verticalConfigSignature":"sig"}""")!.AsObject();
        var p = InstallTokenService.ParseVerticalConfigFromData(data); // must not throw
        Assert.NotNull(p.Raw); Assert.Null(p.Dto); Assert.Equal("sig", p.Signature);
    }
    [Fact] public void Valid_field_parses_dto_and_carries_sig_and_keyid() {
        var data = JsonNode.Parse("""{"verticalConfig":{"vertical":"default","complianceMode":"none","systemConnector":"none","connectorLabel":"your system","redactionProfileId":"none","framing":{"productNoun":"SuavoAgent","systemNoun":"your system","businessNoun":"business","idLabel":"License ID"},"compliance":{"baaRequired":false,"consentCopyId":"terms-v1"}},"verticalConfigSignature":"sig","verticalConfigKeyId":"vertical-v1"}""")!.AsObject();
        var p = InstallTokenService.ParseVerticalConfigFromData(data);
        Assert.NotNull(p.Dto); Assert.Equal("none", p.Dto!.ComplianceMode); Assert.Equal("vertical-v1", p.KeyId);
    }
}
```

- [ ] **Step 2: Run test to verify it fails** — Expected: FAIL (types/method missing).

- [ ] **Step 3: Implement the DTO**

```csharp
// VerticalConfigDto.cs
using System.Text.Json.Serialization;
namespace SuavoAgent.Setup;
public sealed record VerticalFraming(
    [property: JsonPropertyName("productNoun")] string ProductNoun,
    [property: JsonPropertyName("systemNoun")] string SystemNoun,
    [property: JsonPropertyName("businessNoun")] string BusinessNoun,
    [property: JsonPropertyName("idLabel")] string IdLabel);
public sealed record VerticalCompliance(
    [property: JsonPropertyName("baaRequired")] bool BaaRequired,
    [property: JsonPropertyName("consentCopyId")] string ConsentCopyId);
public sealed record VerticalConfigDto(
    [property: JsonPropertyName("vertical")] string Vertical,
    [property: JsonPropertyName("complianceMode")] string ComplianceMode,
    [property: JsonPropertyName("systemConnector")] string SystemConnector,
    [property: JsonPropertyName("connectorLabel")] string ConnectorLabel,
    [property: JsonPropertyName("redactionProfileId")] string RedactionProfileId,
    [property: JsonPropertyName("framing")] VerticalFraming Framing,
    [property: JsonPropertyName("compliance")] VerticalCompliance Compliance) {
    [JsonIgnore] public bool IsValid =>
        !string.IsNullOrWhiteSpace(Vertical) && ComplianceMode is "hipaa" or "pci" or "none"
        && SystemConnector is "pioneerrx" or "none" && Framing is not null && Compliance is not null;
}
```

- [ ] **Step 4: Implement a SHARED parse helper** (used by BOTH the register and device-token paths so they can't drift):

```csharp
// In a small static helper, e.g. VerticalConfigDto.cs or InstallTokenService.cs
public sealed record ParsedVerticalConfig(string? Raw, VerticalConfigDto? Dto, string? Signature, string? KeyId);

public static ParsedVerticalConfig ParseVerticalConfigFromData(JsonObject? data) {
    if (data is null || !data.TryGetPropertyValue("verticalConfig", out var vcNode) || vcNode is null)
        return new ParsedVerticalConfig(null, null, null, null); // (a) absent
    var raw = vcNode.ToJsonString();
    VerticalConfigDto? dto = null;
    try { dto = JsonSerializer.Deserialize<VerticalConfigDto>(raw); } catch (JsonException) { /* (c) malformed */ }
    var sig = data.TryGetPropertyValue("verticalConfigSignature", out var s) ? s?.GetValue<string?>() : null;
    var keyId = data.TryGetPropertyValue("verticalConfigKeyId", out var k) ? k?.GetValue<string?>() : null;
    return new ParsedVerticalConfig(raw, dto, sig, keyId);
}
```

In `InstallTokenService.ExchangeAsync` (after `reasoning`) call `ParseVerticalConfigFromData(data)` and copy its four fields onto `InstallTokenExchangeResult`. **Never throws** (fail-soft); presence/validity is decided later in Task 6.

- [ ] **Step 5: Mirror the parse in the DEVICE-CODE path.** In `DeviceCodeService.cs` (~line 132, where it currently drops unknown fields), call the SAME `ParseVerticalConfigFromData(data)` and surface the four fields on its result type. In `DeviceCodePairing.cs` (~line 101), pass them into the `SetupConfig` constructor (today it builds `SetupConfig` without vertical data). A device-code install MUST end up with the same vertical fields as a token install.

- [ ] **Step 6: Thread through** `SetupConfig` (add `[JsonIgnore] string? VerticalConfigRaw = null`, `[JsonIgnore] VerticalConfigDto? VerticalConfig = null`, `[JsonIgnore] string? VerticalConfigSignature = null`, `[JsonIgnore] string? VerticalConfigKeyId = null`) and `ConnectingViewModel` (pass all four from `result`, same pattern as `Reasoning`). Add a test asserting `ParseVerticalConfigFromData` returns `Raw==null` for absent, `Dto==null && Raw!=null` for malformed, and a full DTO for valid.

- [ ] **Step 7: Run tests to verify they pass** — Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/SuavoAgent.Setup/VerticalConfigDto.cs src/SuavoAgent.Setup/InstallTokenService.cs src/SuavoAgent.Setup/DeviceCodeService.cs src/SuavoAgent.Setup/DeviceCodePairing.cs src/SuavoAgent.Setup/SetupConfig.cs src/SuavoAgent.Setup/Gui/ViewModels/ConnectingViewModel.cs tests/SuavoAgent.Setup.Tests/VerticalConfigParseTests.cs
git commit -m "feat(setup): parse + thread signed verticalConfig from /register AND device-code (fail-soft, presence-aware)"
```

---

### Task 3: VerticalConfigVerifier (RFC-8785 canonical + DER verify, DEDICATED embedded trust store)

**Files:**
- Create: `src/SuavoAgent.Setup/VerticalConfigVerifier.cs`
- Create: `src/SuavoAgent.Setup/Resources/vertical-config-signing-key-vertical-v1.pub.pem` (embedded resource; Joshua commits the real public key — for now a placeholder PEM, tests use the internal overload with a generated key)
- Modify: `src/SuavoAgent.Setup/SuavoAgent.Setup.csproj` (mark the `.pub.pem` as `<EmbeddedResource>`)
- Test: `tests/SuavoAgent.Setup.Tests/VerticalConfigVerifierTests.cs`

**Interfaces:**
- Consumes: a DEDICATED vertical-config trust store of embedded `vertical-config-signing-key-<keyId>.pub.pem` resources (mirror `RulesetSignatureVerifier.LoadEmbeddedTrustStore`, `:94-134`); the in-repo RFC-8785 `JsonCanonicalizer` (used by `RulesetSignatureVerifier.CanonicalizeForSigning`, `:237-241` — add a Setup→Diagnostics project ref, or move the single canonicalizer file to a shared project). Do **NOT** use `BinaryDownloader`'s OTA key (it's `private`, and key separation is the point).
- Produces:
  - `static string Canonicalize(VerticalConfigDto dto)` — RFC-8785 canonical JSON of the DTO (must equal the cloud's `canonicalVerticalJson` output byte-for-byte).
  - `static bool Verify(VerticalConfigDto dto, string? base64Signature, string? keyId)` — `false` unless: `keyId` resolves to a known embedded public key AND `base64Signature` is non-empty AND DER verify passes over the canonical bytes. Unknown keyId / null sig → `false` (caller fails closed).
  - `internal static bool VerifyWithKey(dto, sig, publicKeyBase64)` — test seam (inject a generated key).

- [ ] **Step 1: Write the failing test** — generate a P-256 keypair, sign the canonical bytes with DER, assert verify via the internal `VerifyWithKey` seam; plus the cross-repo golden vector.

```csharp
// tests/SuavoAgent.Setup.Tests/VerticalConfigVerifierTests.cs
using System.Security.Cryptography;
using System.Text;
using SuavoAgent.Setup;
using Xunit;

public class VerticalConfigVerifierTests {
    private static VerticalConfigDto Sample() => new("default","none","none","your system","none",
        new("SuavoAgent","your system","business","License ID"), new(false,"terms-v1"));

    [Fact] public void Valid_der_signature_verifies() {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pub = Convert.ToBase64String(ec.ExportSubjectPublicKeyInfo());
        var sig = ec.SignData(Encoding.UTF8.GetBytes(VerticalConfigVerifier.Canonicalize(Sample())),
            HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        Assert.True(VerticalConfigVerifier.VerifyWithKey(Sample(), Convert.ToBase64String(sig), pub));
    }
    [Fact] public void Tampered_config_rejected() {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pub = Convert.ToBase64String(ec.ExportSubjectPublicKeyInfo());
        var sig = ec.SignData(Encoding.UTF8.GetBytes(VerticalConfigVerifier.Canonicalize(Sample())),
            HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        Assert.False(VerticalConfigVerifier.VerifyWithKey(Sample() with { ComplianceMode = "hipaa" }, Convert.ToBase64String(sig), pub));
    }
    [Fact] public void Null_signature_rejected() =>
        Assert.False(VerticalConfigVerifier.VerifyWithKey(Sample(), null, "ignored"));
    [Fact] public void Unknown_keyid_rejected() =>
        Assert.False(VerticalConfigVerifier.Verify(Sample(), "AAAA", "no-such-key"));

    // CROSS-REPO GOLDEN — must match the cloud vertical-config-signing.test.ts golden string
    // EXACTLY (RFC 8785 == ES JSON.stringify, so & < > + é are NOT escaped). If C# STJ's
    // default encoder over-escapes here, the JsonCanonicalizer parse step washes it out; this
    // [Fact] is the proof. If it ever fails, switch Canonicalize to canonicalize the RAW
    // received JSON string, not a re-serialized DTO.
    [Fact] public void Golden_escaping_vector_matches_cloud() {
        // The cloud asserts: canonicalVerticalJson({z:'a&b<c>d+e', a:'café', m:'plain'})
        //   === '{"a":"café","m":"plain","z":"a&b<c>d+e"}'
        Assert.Equal("{\"a\":\"café\",\"m\":\"plain\",\"z\":\"a&b<c>d+e\"}",
            VerticalConfigVerifier.CanonicalizeRaw("{\"z\":\"a&b<c>d+e\",\"a\":\"café\",\"m\":\"plain\"}"));
    }
}
```

- [ ] **Step 2: Run test to verify it fails** — Expected: FAIL.

- [ ] **Step 3: Implement**

```csharp
// VerticalConfigVerifier.cs
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
// using <JsonCanonicalizer namespace>;
namespace SuavoAgent.Setup;
public static class VerticalConfigVerifier {
    private const string ResourcePrefix = "vertical-config-signing-key-";  // + keyId + ".pub.pem"
    private const string ResourceSuffix = ".pub.pem";

    public static string Canonicalize(VerticalConfigDto dto) =>
        CanonicalizeRaw(JsonSerializer.Serialize(dto)); // [JsonPropertyName] => cloud key names

    // Canonicalize a RAW JSON string: the JsonCanonicalizer PARSES then re-emits per RFC 8785,
    // so any STJ over-escaping in the intermediate string is washed out (proven by the golden vector).
    public static string CanonicalizeRaw(string json) => JsonCanonicalizer.GetEncodedString(json);

    public static bool Verify(VerticalConfigDto dto, string? base64Signature, string? keyId) {
        if (string.IsNullOrWhiteSpace(keyId)) return false;
        var pub = LoadEmbeddedPublicKey(keyId!);            // null => unknown keyId => fail-closed
        return pub is not null && VerifyWithKey(dto, base64Signature, pub);
    }

    internal static bool VerifyWithKey(VerticalConfigDto dto, string? base64Signature, string publicKeyBase64) {
        if (string.IsNullOrWhiteSpace(base64Signature)) return false;
        try {
            using var ec = ECDsa.Create();
            ec.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
            return ec.VerifyData(Encoding.UTF8.GetBytes(Canonicalize(dto)),
                Convert.FromBase64String(base64Signature), HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
        } catch { return false; }
    }

    private static string? LoadEmbeddedPublicKey(string keyId) {
        // mirror RulesetSignatureVerifier.LoadEmbeddedTrustStore: resource name endswith prefix+keyId+suffix
        var asm = Assembly.GetExecutingAssembly();
        var name = Array.Find(asm.GetManifestResourceNames(),
            n => n.EndsWith($"{ResourcePrefix}{keyId}{ResourceSuffix}", StringComparison.Ordinal));
        if (name is null) return null;
        using var s = asm.GetManifestResourceStream(name)!;
        using var r = new StreamReader(s);
        using var ec = ECDsa.Create();
        ec.ImportFromPem(r.ReadToEnd());                    // PEM => key
        return Convert.ToBase64String(ec.ExportSubjectPublicKeyInfo()); // normalize to base64 SPKI for VerifyWithKey
    }
}
```

> `VerifyWithKey` takes a base64 SPKI public key (the test seam). `LoadEmbeddedPublicKey` reads the embedded PEM, imports it, and re-exports SPKI base64 — so production verify and the test seam share one verify path. The `Golden_escaping_vector` `[Fact]` is the load-bearing cross-repo lock: it MUST equal the cloud's golden string.

- [ ] **Step 4: Run test to verify it passes** — Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/SuavoAgent.Setup/VerticalConfigVerifier.cs src/SuavoAgent.Setup/Resources src/SuavoAgent.Setup/SuavoAgent.Setup.csproj tests/SuavoAgent.Setup.Tests/VerticalConfigVerifierTests.cs
git commit -m "feat(setup): verify signed verticalConfig (RFC-8785 canonical + DER, dedicated embedded trust store)"
```

---

### Task 4: CompliancePosture (fail-closed + downgrade-refusal) + block override paths

**Files:**
- Create: `src/SuavoAgent.Core/Compliance/CompliancePosture.cs`, `LastKnownGoodStore.cs`
- Modify: `src/SuavoAgent.Core/Cloud/ConfigOverrideStore.cs` (extend `BlockedExactPaths`)
- Test: `tests/SuavoAgent.Core.Tests/Compliance/CompliancePostureTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public enum ComplianceMode { None = 0, Pci = 1, Hipaa = 2 } // value = strictness rank
  public static class CompliancePosture {
      public static ComplianceMode Parse(string? s);                  // unknown/null => Hipaa (fail-closed)
      public static ComplianceMode Resolve(ComplianceMode incoming, ComplianceMode lastKnownGood); // max(incoming,lkg)
  }
  ```
  `Resolve` returns the **stricter** of incoming vs last-known-good (downgrade refusal). `Parse(null)` / unknown → `Hipaa`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SuavoAgent.Core.Tests/Compliance/CompliancePostureTests.cs
using SuavoAgent.Core.Compliance;
using Xunit;
public class CompliancePostureTests {
    [Theory]
    [InlineData("hipaa", ComplianceMode.Hipaa)]
    [InlineData("pci", ComplianceMode.Pci)]
    [InlineData("none", ComplianceMode.None)]
    [InlineData(null, ComplianceMode.Hipaa)]      // fail-closed
    [InlineData("garbage", ComplianceMode.Hipaa)] // fail-closed
    public void Parse_fails_closed(string? s, ComplianceMode expected) => Assert.Equal(expected, CompliancePosture.Parse(s));

    [Fact] public void Refuses_downgrade_below_last_known_good() =>
        Assert.Equal(ComplianceMode.Hipaa, CompliancePosture.Resolve(ComplianceMode.None, ComplianceMode.Hipaa));
    [Fact] public void Accepts_upgrade() =>
        Assert.Equal(ComplianceMode.Hipaa, CompliancePosture.Resolve(ComplianceMode.Hipaa, ComplianceMode.None));
    [Fact] public void Allows_none_when_no_prior_good() =>
        Assert.Equal(ComplianceMode.None, CompliancePosture.Resolve(ComplianceMode.None, ComplianceMode.None));
}
```

- [ ] **Step 2: Run test to verify it fails** — Expected: FAIL.

- [ ] **Step 3: Implement**

```csharp
// CompliancePosture.cs
namespace SuavoAgent.Core.Compliance;
public enum ComplianceMode { None = 0, Pci = 1, Hipaa = 2 }
public static class CompliancePosture {
    public static ComplianceMode Parse(string? s) => s switch {
        "hipaa" => ComplianceMode.Hipaa,
        "pci" => ComplianceMode.Pci,
        "none" => ComplianceMode.None,
        _ => ComplianceMode.Hipaa, // fail-closed: null/unknown => strictest
    };
    public static ComplianceMode Resolve(ComplianceMode incoming, ComplianceMode lastKnownGood) =>
        (ComplianceMode)Math.Max((int)incoming, (int)lastKnownGood); // downgrade refusal
}
```

```csharp
// LastKnownGoodStore.cs — persists to %ProgramData%/SuavoAgent/compliance-lkg.json (ACL-protected dir).
// ponytail: a single int rank in one JSON file; the dir is already ACL-locked in Program.cs.
namespace SuavoAgent.Core.Compliance;
public sealed class LastKnownGoodStore {
    private readonly string _path;
    public LastKnownGoodStore(string dataDir) =>
        _path = System.IO.Path.Combine(dataDir, "compliance-lkg.json");
    public ComplianceMode Read() {
        try { return System.IO.File.Exists(_path)
            ? CompliancePosture.Parse(System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(_path)).RootElement.GetProperty("mode").GetString())
            : ComplianceMode.None; }   // no prior good => no floor (first install)
        catch { return ComplianceMode.Hipaa; } // unreadable => fail-closed
    }
    public void Write(ComplianceMode mode) =>
        System.IO.File.WriteAllText(_path, $"{{\"mode\":\"{mode.ToString().ToLowerInvariant()}\"}}");
}
```

- [ ] **Step 4: Block compliance override paths** in `ConfigOverrideStore.cs` — append to `BlockedExactPaths`: `"Agent.ComplianceMode"`, `"Agent.VerticalConfig"`, `"Agent.SystemConnector"`. Add a test in the existing `ConfigOverrideStore` test that an override on `Agent.ComplianceMode` is rejected (cloud config-push must NOT be able to weaken posture out-of-band).

- [ ] **Step 5: Run tests to verify they pass** — Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/SuavoAgent.Core/Compliance src/SuavoAgent.Core/Cloud/ConfigOverrideStore.cs tests/SuavoAgent.Core.Tests/Compliance
git commit -m "feat(core): CompliancePosture fail-closed + downgrade-refusal; block compliance override paths"
```

---

### Task 5: Consent renders per complianceMode + records it in the receipt

**Files:**
- Modify: `src/SuavoAgent.Setup/Gui/ViewModels/ConsentViewModel.cs`
- Modify: `src/SuavoAgent.Setup/Gui/Services/ConsentReceiptData.cs`
- Modify: `src/SuavoAgent.Setup/Gui/Views/ConsentView.axaml` (bind a mode-driven visibility)
- Test: `tests/SuavoAgent.Setup.Tests/ConsentComplianceModeTests.cs`

**Interfaces:**
- Consumes: `complianceMode` (string from the verified `VerticalConfigDto`, resolved by `CompliancePosture`). `ConsentViewModel` gains a `ComplianceMode` (string) input (default `"hipaa"` — fail-closed if unset).
- Produces: `ConsentViewModel` exposes `bool ShowHipaaDisclosure => ComplianceMode == "hipaa"`. For `none`, the heavy HIPAA disclosure + BAA acknowledgment collapse to a single Terms checkbox; the existing US-state notice logic still runs **only** when `ShowHipaaDisclosure`. `ConsentReceiptData` gains `string ComplianceMode` and serializes it (never omitted).

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SuavoAgent.Setup.Tests/ConsentComplianceModeTests.cs
using SuavoAgent.Setup.Gui.ViewModels;
using SuavoAgent.Setup.Gui.Services;
using Xunit;
public class ConsentComplianceModeTests {
    [Fact] public void Hipaa_mode_shows_disclosure_and_requires_baa() {
        var vm = new ConsentViewModel(/*existing ctor args*/) { ComplianceMode = "hipaa", Name = "A", StateCode = "TX", AgreedToTerms = true };
        Assert.True(vm.ShowHipaaDisclosure);
    }
    [Fact] public void None_mode_minimal_terms_only() {
        var vm = new ConsentViewModel(/*existing ctor args*/) { ComplianceMode = "none", Name = "A", StateCode = "TX", AgreedToTerms = true };
        Assert.False(vm.ShowHipaaDisclosure);
        Assert.True(vm.AgreeCommand.CanExecute(null)); // name + terms enough; no BAA/notice gate in none mode
    }
    [Fact] public void Receipt_records_compliance_mode() {
        var r = new ConsentReceiptData("A","Owner","TX", MandatoryNoticeState:false, EmployeeNoticeAcknowledged:true, Timestamp:default, ComplianceMode:"none");
        Assert.Contains("\"complianceMode\":\"none\"", r.ToJson("ph","ag","v1","fp"));
    }
}
```

- [ ] **Step 2: Run test to verify it fails** — Expected: FAIL (no `ComplianceMode`/`ShowHipaaDisclosure`).

- [ ] **Step 3: Implement** — add `ComplianceMode` (default `"hipaa"`) + `ShowHipaaDisclosure` to `ConsentViewModel`; gate `RequiresEmployeeNotice`/BAA on `ShowHipaaDisclosure`; for `none`, `AgreeCommand.CanExecute` requires only `Name` + `AgreedToTerms`. Add `ComplianceMode` to the `ConsentReceiptData` record + `ToJson` (always serialized). In `ConsentView.axaml`, bind the HIPAA disclosure card + employee-notice checkbox `IsVisible` to `ShowHipaaDisclosure`.

- [ ] **Step 4: Run test to verify it passes** — Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/SuavoAgent.Setup/Gui/ViewModels/ConsentViewModel.cs src/SuavoAgent.Setup/Gui/Services/ConsentReceiptData.cs src/SuavoAgent.Setup/Gui/Views/ConsentView.axaml tests/SuavoAgent.Setup.Tests/ConsentComplianceModeTests.cs
git commit -m "feat(setup): consent branches hipaa disclosure vs minimal Terms; receipt records complianceMode"
```

---

### Task 6: Wire it together — verify → posture → connector selection → bake; fail-closed at install

**Files:**
- Modify: `src/SuavoAgent.Setup/Gui/Services/InstallOrchestrator.cs` (`BuildAppSettings` + connector selection)
- Modify: `src/SuavoAgent.Setup/ConsoleInstaller.cs` (replace direct PioneerRx calls with the connector)
- Test: `tests/SuavoAgent.Setup.Tests/InstallVerticalWiringTests.cs`

**Interfaces:**
- Consumes: `SetupConfig.VerticalConfigRaw/.VerticalConfig/.VerticalConfigSignature/.VerticalConfigKeyId` (Task 2), `VerticalConfigVerifier.Verify` (Task 3), `CompliancePosture` + `LastKnownGoodStore` (Task 4), `SystemConnectorFactory.Select` (Task 1), `ConsentViewModel.ComplianceMode` (Task 5).
- Produces: a pure `static InstallPosture ResolveInstallPosture(bool rawPresent, VerticalConfigDto? dto, string? signature, string? keyId, ComplianceMode lkg, string? publicKeyOverride = null)` returning `record InstallPosture(bool Blocked, ComplianceMode EffectiveMode, ISystemConnector Connector, string? Reason)`. The **five-state matrix** (Codex-hardened — never confuses malformed with absent, never bricks the rollout window):
  1. **`!rawPresent`** (field absent — legacy cloud) → NOT blocked; `EffectiveMode = Resolve(Hipaa, lkg)`; `Connector = PioneerRxConnector` (back-compat). Do NOT write lkg.
  2. **`rawPresent && IsNullOrEmpty(signature)`** (rollout window — cloud key not live) → same as (1): back-compat PioneerRx + hipaa (fail-closed-to-hipaa, non-bricking). Do NOT write lkg.
  3. **`rawPresent && !IsNullOrEmpty(signature) && dto == null`** (present + signed + MALFORMED) → **Blocked** (tamper/corruption).
  4. **`rawPresent && !IsNullOrEmpty(signature) && dto != null && !Verify(dto, signature, keyId)`** (bad signature / unknown keyId) → **Blocked**.
  5. **verified** (`dto != null && Verify` passes) → honor: `incoming = CompliancePosture.Parse(dto.ComplianceMode)`; `EffectiveMode = Resolve(incoming, lkg)`; `Connector = SystemConnectorFactory.Select(dto.SystemConnector)` — wrap in try/catch `UnknownConnectorException` → **Blocked**. Persist `lkg.Write(EffectiveMode)` (only here, only on verified). **Never silently runs PioneerRx for an unverified config** (states 3–4 block).
- Then bake `agent["ComplianceMode"] = EffectiveMode` + `agent["VerticalConfig"]` (connector key, redactionProfileId, signature, keyId) into appsettings via a new `BakeVerticalConfig(agent, ...)` next to `BakeReasoning`; pass `EffectiveMode` into `ConsentViewModel.ComplianceMode`.

- [ ] **Step 1: Write the failing test** (all five states + downgrade refusal)

```csharp
// tests/SuavoAgent.Setup.Tests/InstallVerticalWiringTests.cs
using System.Security.Cryptography;
using System.Text;
using SuavoAgent.Setup;
using SuavoAgent.Setup.Connectors;
using SuavoAgent.Core.Compliance;
using Xunit;

public class InstallVerticalWiringTests {
    private static VerticalConfigDto Default() => new("default","none","none","your system","none",
        new("SuavoAgent","your system","business","License ID"), new(false,"terms-v1"));
    private static (string sig, string pub) SignWith(VerticalConfigDto d) {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var sig = ec.SignData(Encoding.UTF8.GetBytes(VerticalConfigVerifier.Canonicalize(d)),
            HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return (Convert.ToBase64String(sig), Convert.ToBase64String(ec.ExportSubjectPublicKeyInfo()));
    }

    [Fact] public void State1_absent_is_backcompat_pioneerrx_hipaa() {
        var r = InstallOrchestrator.ResolveInstallPosture(false, null, null, null, ComplianceMode.None);
        Assert.False(r.Blocked); Assert.Equal(ComplianceMode.Hipaa, r.EffectiveMode); Assert.Equal("pioneerrx", r.Connector.Key);
    }
    [Fact] public void State2_present_unsigned_is_backcompat_not_bricked() {
        var r = InstallOrchestrator.ResolveInstallPosture(true, Default(), signature: null, keyId: null, ComplianceMode.None);
        Assert.False(r.Blocked); Assert.Equal(ComplianceMode.Hipaa, r.EffectiveMode); Assert.Equal("pioneerrx", r.Connector.Key);
    }
    [Fact] public void State3_present_signed_malformed_blocks() {
        var r = InstallOrchestrator.ResolveInstallPosture(true, dto: null, signature: "AAAA", keyId: "vertical-v1", ComplianceMode.None);
        Assert.True(r.Blocked);
    }
    [Fact] public void State4_present_bad_signature_blocks() {
        var (_, pub) = SignWith(Default());
        var r = InstallOrchestrator.ResolveInstallPosture(true, Default(), signature: "AAAA", keyId: "vertical-v1", ComplianceMode.None, publicKeyOverride: pub);
        Assert.True(r.Blocked);
    }
    [Fact] public void State5_verified_default_selects_null_none() {
        var (sig, pub) = SignWith(Default());
        var r = InstallOrchestrator.ResolveInstallPosture(true, Default(), sig, "vertical-v1", ComplianceMode.None, publicKeyOverride: pub);
        Assert.False(r.Blocked); Assert.Equal("none", r.Connector.Key); Assert.Equal(ComplianceMode.None, r.EffectiveMode);
    }
    [Fact] public void State5_verified_downgrade_is_refused() {
        var (sig, pub) = SignWith(Default());
        var r = InstallOrchestrator.ResolveInstallPosture(true, Default(), sig, "vertical-v1", ComplianceMode.Hipaa, publicKeyOverride: pub);
        Assert.Equal(ComplianceMode.Hipaa, r.EffectiveMode); // refused downgrade to none
    }
}
```

> `ResolveInstallPosture` takes a `publicKeyOverride` test seam: when non-null it verifies via `VerticalConfigVerifier.VerifyWithKey(dto, signature, publicKeyOverride)`; when null it uses `VerticalConfigVerifier.Verify(dto, signature, keyId)` (embedded trust store). It must NOT touch the lkg file in tests — accept the `lkg` value as a parameter and return the resolved mode without writing (the caller in `RunAsync` does the `lkg.Write`).

- [ ] **Step 2: Run test to verify it fails** — Expected: FAIL (`ResolveInstallPosture` missing).

- [ ] **Step 3: Implement** `ResolveInstallPosture` encoding the five-state matrix above (pure, no file I/O). Then call it from `InstallOrchestrator.RunAsync`/`BuildAppSettings` (GUI) and `ConsoleInstaller.RunAsync` (console): pass `(VerticalConfigRaw != null, VerticalConfig, VerticalConfigSignature, VerticalConfigKeyId, lkg.Read())`; on `!Blocked && verified` do `lkg.Write(posture.EffectiveMode)`; replace the direct `PioneerRxDiscovery.Discover()` / `SqlCredentialDiscovery.TryAutoDiscover()` calls with `posture.Connector.Probe()` / `.Discover(probe)`; pass `posture.EffectiveMode` (string) into `ConsentViewModel.ComplianceMode`; add `BakeVerticalConfig`. On `Blocked`, surface "couldn't verify your account configuration — contact support" and abort (do NOT write services).

> Keep the existing `PmsDetected` flag + SQL-key-omission contract from `BuildAgentConfig` intact — `NullConnector` yields no creds, so SQL keys are omitted exactly as the no-PMS path does today (the `ConsoleInstallerConfigTests` must stay green).

- [ ] **Step 4: Run tests to verify they pass** — Run the Setup suite; Expected: PASS, and existing `ConsoleInstallerConfigTests` + `BinaryDownloaderTests` stay green.

- [ ] **Step 5: Commit**

```bash
git add src/SuavoAgent.Setup/Gui/Services/InstallOrchestrator.cs src/SuavoAgent.Setup/ConsoleInstaller.cs tests/SuavoAgent.Setup.Tests/InstallVerticalWiringTests.cs
git commit -m "feat(setup): wire verify->posture->connector with fail-closed-to-HIPAA + downgrade refusal"
```

---

### Task 7: Full-suite gate

- [ ] **Step 1:** `dotnet test SuavoAgent.sln --configuration Release` — Expected: ALL green (new + existing).
- [ ] **Step 2:** Confirm the cross-repo golden-canonical assert (Task 3) matches the cloud `canonicalVerticalJson` output for the `default` config. If it drifts, fix the DTO field names/order — **the signature contract depends on byte-identical canonical forms.**
- [ ] **Step 3: Commit** any test-only fixups.

---

## Self-Review

- **Spec coverage:** `ISystemConnector` + PioneerRx/Null (T1) ✓ · parse signed config (T2) ✓ · signature verify (T3) ✓ · fail-closed-to-HIPAA + downgrade refusal (T4, T6) ✓ · consent per complianceMode + audit record (T5) ✓ · connector selection by config, no-silent-PioneerRx on tamper (T6) ✓ · read-only/no-override (T4 blocked paths) ✓.
- **Placeholders:** ConsentViewModel ctor args are `/*existing*/` because the real signature must be read from the file — the behavioral asserts are concrete. `SignedDefault()`/`TestPubKey` helpers are described (sign with a test key, inject via `publicKeyOverride`).
- **Type consistency:** `ComplianceMode` enum (ranked) used identically T4/T6; `VerticalConfigDto` field names match the cloud `VerticalConfig` 1:1 (T2/T3); `ISystemConnector.Key` values `"pioneerrx"`/`"none"` consistent T1/T6.
- **Cross-repo contract (load-bearing):** the agent canonicalizes the DTO with `JsonCanonicalizer` (RFC 8785); the cloud signs `canonicalVerticalJson` (sorted-key, number-free). For this slice's number-free payload they are byte-identical (RFC 8785 string rules ARE ES `JSON.stringify`). The `<>&+é` golden-string assert (cloud T2 ↔ agent T3) locks it empirically. DEDICATED signing key = `SUAVO_VERTICAL_CONFIG_SIGNING_KEY` (cloud) / embedded `vertical-config-signing-key-vertical-v1.pub.pem` trust store (agent), selected by `verticalConfigKeyId`. **Adding a field ⇒ add on both sides, keep non-numeric, update the golden string.**
- **Codex SHOULD-FIX items resolved:** dedicated key (was OTA-key reuse) ✓ · device-code path wired (DeviceCodeService + DeviceCodePairing) ✓ · present-malformed vs absent distinguished via `VerticalConfigRaw` + the 5-state matrix ✓ · rollout window non-bricking (state 2) ✓ · LKG is in-lifetime defense-in-depth, cloud is the real floor (documented) ✓ · escaping golden vector ✓ · `BinaryDownloader.PublicKeyBase64`-private nit dissolved by the dedicated trust store ✓.
- **Runtime safety:** the strict-by-default runtime (actuation off, vision off, PHI policy enforced) is untouched; a `default` install differs only by NullConnector + minimal consent + recorded mode. No PHI machinery is disabled.
