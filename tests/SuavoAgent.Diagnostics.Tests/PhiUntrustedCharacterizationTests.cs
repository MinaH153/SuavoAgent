using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using SuavoAgent.Diagnostics;
using Xunit;

namespace SuavoAgent.Diagnostics.Tests;

/// <summary>
/// PHI single-source merge — UNTRUSTED-path byte-exact characterization gate
/// (blueprint fix #7). Snapshots the EXACT output of the OTA-hot-swap
/// <see cref="PhiScrubber.Sanitize"/> across a corpus exercising every untrusted rule
/// (PioneerRx JSON/XML/SQL, the 5 NDC hyphenations, BCBS/Aetna/Cigna member IDs,
/// field-context NPI, checksum-gated DEA, SSN, narrow DOB-shape, Windows user path).
///
/// The merge re-sources <c>BuildRules</c> from the shared <c>PhiRuleCatalog</c>. This gate
/// fails (RED) the instant the NonBacktracking pipeline diverges from the snapshot by a
/// single byte. The fixture <c>phi-golden-untrusted.json</c> was generated from the
/// PRE-merge engine and is the immovable baseline.
///
/// Regenerate ONLY against known-good pre-merge code:
///   PHI_GOLDEN_REGEN=1 PHI_GOLDEN_REGEN_OUTPUT=/absolute/path/to/phi-golden-untrusted.json
///     dotnet test --filter FullyQualifiedName~PhiUntrustedCharacterization
/// </summary>
public sealed class PhiUntrustedCharacterizationTests
{
    public static readonly string[] Corpus =
    {
        // ── PioneerRx field-context (JSON / XML / SQL) ──
        "event payload: \"PatientID\":\"P12345\" arrived at 12:00",
        "event payload: \"RxNumber\":\"P12345\" arrived at 12:00",
        "event payload: \"PrescriberID\":\"P12345\" arrived at 12:00",
        "event payload: \"PharmacyChainID\":\"P12345\" arrived at 12:00",
        "<root><RxNumber>RX1234567</RxNumber></root>",
        "<root><PatientID>RX1234567</PatientID></root>",
        "SELECT * FROM rx WHERE PatientID='P12345' AND status='active'",
        "SELECT * FROM rx WHERE RxNumber='P12345' AND status='active'",
        "{\"PatientID\":\"John Doe, MRN 9\"}",

        // ── NDC variants (5 hyphenations + unhyphenated-with-label) ──
        "ndc=1234-5678-90",
        "12345-6789-01 dispensed",
        "drug 12345-678-90 ok",
        "pkg 12345-6789-0",
        "\"NDC\":\"12345678901\"",
        "ndc=12345678901",
        "National_Drug_Code: 12345678901",

        // ── Insurance member IDs (field-context) ──
        "BCBS member ABC123456789 dispensed",
        "blue cross MEM123456 OK",
        "Aetna ID W123456789 valid",
        "Cigna member U123456789 valid",

        // ── Prescriber NPI (field-context) ──
        "PrescriberNPI=1234567893",
        "provider_npi: 1234567893",
        "\"npi\":\"1234567893\"",

        // ── Rx-number (PioneerRx) — runs before DEA shape ──
        "prescription RX1234567 filled",
        "prescription rx987654321 filled",
        "prescription RX12345678901 filled",

        // ── DEA (shape + checksum validator) ──
        "prescriber DEA AB1234563 wrote",  // valid checksum
        "prescriber DEA AB1111111 wrote",  // invalid checksum — must NOT redact on untrusted path
        "DEA XY7654321 issued",

        // ── Standard PHI shapes ──
        "patient ssn 123-45-6789 confirmed",
        "patient DOB 01/15/1985 reviewed",
        "patient DOB 1/15/1985 reviewed",
        "patient DOB 12-31-2024 reviewed",
        "patient DOB 12/31/2024 reviewed",
        "born 1990-01-15 today",           // ISO bare date — narrow DOB-shape does NOT match

        // ── Windows user path ──
        "C:\\Users\\joshua\\Code\\SuavoAgent\\src\\file.cs",
        "/Users/joshua/Documents/data.json",

        // ── negatives / plain / common words ──
        "Workflow completed successfully with no PHI present in this string.",
        "The May release Will ship in King City after Brown gives Price approval.",
        "Point of Sale",
        "PioneerRx - Pharmacy Management",
        "Version 2.0.0 Build 1234",

        // ── cross-path: trusted-style inputs on the untrusted engine ──
        "Patient: Jane Doe",
        "Address: 1234 Maple Street, Apt 2",
        "Member ID: ABC123",
        "Call 555-123-4567",

        // ── empty / whitespace ──
        "",
        "   ",
    };

    private sealed class GoldenEntry
    {
        public string Input { get; set; } = "";
        public string Sanitize { get; set; } = "";
    }

    private static PhiScrubber Scrubber()
        // Generous 5s deadline: this gate characterizes the SCRUBBING OUTPUT, not the timeout.
        // The NonBacktracking DFA is built lazily on first match and that cold cost — which counts
        // against the per-rule deadline — can exceed a tight budget on a cold/slow CI runner and
        // spuriously return [SCRUB_TIMEOUT] (observed in CI at 200ms). 5s removes that flakiness
        // while still bounding a genuine hang.
        => new(new RulesetV1(), TimeSpan.FromSeconds(5));

    private static string GoldenPath()
        => Path.Combine(AppContext.BaseDirectory, "phi-golden-untrusted.json");

    private static string RegenerationPath()
    {
        var path = Environment.GetEnvironmentVariable("PHI_GOLDEN_REGEN_OUTPUT");
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidOperationException(
                "PHI_GOLDEN_REGEN_OUTPUT must be an absolute reviewed fixture path.");
        return Path.GetFullPath(path);
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [Fact]
    public void Untrusted_scrubber_output_is_byte_exact_with_the_golden_snapshot()
    {
        if (Environment.GetEnvironmentVariable("PHI_GOLDEN_REGEN") == "1")
        {
            var regen = new List<GoldenEntry>();
            var s = Scrubber();
            foreach (var input in Corpus)
                regen.Add(new GoldenEntry { Input = input, Sanitize = s.Sanitize(input) });
            File.WriteAllText(RegenerationPath(), JsonSerializer.Serialize(regen, JsonOpts));
            return;
        }

        var golden = JsonSerializer.Deserialize<List<GoldenEntry>>(File.ReadAllText(GoldenPath()))!;
        Assert.Equal(Corpus.Length, golden.Count);

        var scrubber = Scrubber();
        for (var i = 0; i < golden.Count; i++)
        {
            var g = golden[i];
            Assert.Equal(Corpus[i], g.Input);
            Assert.Equal(g.Sanitize, scrubber.Sanitize(g.Input));
        }
    }
}
