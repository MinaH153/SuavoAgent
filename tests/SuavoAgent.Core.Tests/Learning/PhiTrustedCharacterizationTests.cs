using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using SuavoAgent.Core.Learning;
using Xunit;

namespace SuavoAgent.Core.Tests.Learning;

/// <summary>
/// PHI single-source merge — TRUSTED-path byte-exact characterization gate
/// (blueprint fix #7). Snapshots the EXACT output of <see cref="PhiScrubber.ScrubText"/>
/// and the EXACT boolean of <see cref="PhiScrubber.ContainsPhi"/> across a corpus that
/// exercises every trusted rule (lowercase keywords, case-sensitive name shapes,
/// checksum-invalid DEA, multi-line addresses, the AllCaps/ICD/Insurance/MRN
/// capture-group rewrites, and the legacy ContainsPhi true/false cases).
///
/// The merge moves these rules from hand-written <c>[GeneratedRegex]</c> partials into
/// the shared <c>PhiRuleCatalog</c>, materialized by <c>PhiTextScrubber</c>. This gate
/// fails (RED) the instant the refactored engine diverges from the snapshot by a single
/// byte. The fixture <c>phi-golden-trusted.json</c> was generated from the PRE-merge
/// engine and is the immovable baseline.
///
/// Regenerate ONLY against known-good pre-merge code:
///   PHI_GOLDEN_REGEN=1 dotnet test --filter FullyQualifiedName~PhiTrustedCharacterization
/// </summary>
public sealed class PhiTrustedCharacterizationTests
{
    // The corpus. Every entry is a real string the trusted scrubber may see from the
    // vision pipeline / reasoning context. Do NOT prune — each line locks a behavior.
    public static readonly string[] Corpus =
    {
        // ── legacy Core ScrubText cases ──
        "John Smith - Prescription",
        "Patient: Jane Doe",
        "RX for 555-123-4567",
        "DOB: 01/15/1990",
        "SSN 123-45-6789",
        "MRN: ABC12345",
        "Point of Sale",
        "PioneerRx - Pharmacy Management",
        "Rx: John Smith | Status: Ready",
        "Address: John Williams",
        "Point Sale — Status Type",
        "Rx: Jane Doe | ID: 999",
        "Phone: 555-1234 Address: John Williams",

        // ── address keyword cases ──
        "Address: 1234 Maple Street, Apt 2",
        "Address: 1234 Maple Street Apt 2",
        "Address: 1234 Maple St",
        "Addr: 555 Main Street",
        "Mailing Address: 9876 Oak Avenue",
        "ADDRESS: 42 Elm Drive Suite 100",

        // ── bare street-suffix cases ──
        "1234 Maple Street, Apt 2",
        "1234 Maple Street",
        "1234 Maple St.",
        "9876 Oak Avenue",
        "555 Main Boulevard",
        "42 Elm Drive Suite 100",
        "17 N Cherry Lane #4",

        // ── address false positives (must NOT scrub) ──
        "Page 1 of 5",
        "Zone 12 Active",
        "Version 2.0 Build 1234",
        "Window 3 Open",
        "Item 7 Quantity 12",
        "Tab 4 Selected",
        "Aisle 12 Bin 4",
        "12 Months Old Dr Smith",
        "Lot Box 4567",
        "30.5 N 120.3 W Way",
        "Last 3 Records Active",

        // ── address in larger string / adjacency ──
        "Patient verified | Address: 1234 Maple Street | Status: Ready",
        "Address: 1234 Maple Street DOB: 01/15/1990",
        "123 Maple St, Apt 5 PatientID 999",

        // ── PO Box variants ──
        "Address: PO Box 1234",
        "Address: P.O. Box 9876",
        "PO Box 1234, San Diego",
        "P.O.B. 1234",
        "P O Box 1234",
        "PO BOX 9876",
        "p.o. box 5555",

        // ── hyphenated civic / military / rural ──
        "1234-A Main Street",
        "1234-B Oak Avenue",
        "Address: 1234-A Main Street, Apt 2",
        "APO AE 09012",
        "FPO AP 96662",
        "DPO AA 34001",
        "RR 1 Box 5",
        "HC 67 Box 23",
        "Address: RR 2 Box 99",

        // ── ordinal street names ──
        "1234 5th Avenue",
        "567 42nd Street",
        "89 1st Boulevard",
        "100 3rd Court",

        // ── stop-keyword street names ──
        "Address: 123 State Street",
        "Address: 456 City Drive",
        "Address: 789 Insurance Court",
        "Address: 5678 Forest Drive City: San Diego ZIP: 92101 DOB: 03/15/1980",

        // ── health-plan identifiers ──
        "Member ID: ABC123",
        "Subscriber ID: XYZ789",
        "Group: 5500",
        "Policy Number: P-9876",
        "Insurance: BlueCross",
        "Plan: Premium-Gold",

        // ── name shapes (case-sensitive) ──
        "Doe, John",
        "Doe, John A",
        "Doe, John A.",
        "Patient: DOE, JOHN",
        "Name: DOE JOHN A",

        // ── ICD / Dx codes ──
        "Dx: E11.9",
        "Diagnosis: F32.1",
        "ICD: Z79.4",
        "Code: B12",

        // ── identifiers: NPI / DEA (incl. checksum-invalid) / Rx ──
        "NPI: 1234567890",
        "npi 1234567890",
        "prescriber DEA AB1234563 wrote",
        "AB1111111",
        "DEA XY7654321",
        "Rx #12345",
        "RxNo: 12345",
        "Rx Number 123456",

        // ── phone variants ──
        "Call 555-123-4567",
        "(555) 123-4567",
        "5551234567",

        // ── ContainsPhi false cases ──
        "Port 12345",
        "ID: 12345",
        "Version 2.0.0",
        "Page 12 of 50",

        // ── plain / common-word / multi-line / empty ──
        "Workflow completed successfully with no PHI present in this string.",
        "The May release Will ship in King City after Brown gives Price approval.",
        "Address: 1234 Maple Street\nDOB: 01/15/1990",
        "Line one\nPatient: Jane Doe\nLine three",
        "",
        "   ",
    };

    private sealed class GoldenEntry
    {
        public string Input { get; set; } = "";
        public string? Scrub { get; set; }
        public bool Contains { get; set; }
    }

    private static string GoldenPath([CallerFilePath] string thisFile = "")
        => Path.Combine(Path.GetDirectoryName(thisFile)!, "phi-golden-trusted.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        // Keep non-ASCII (em dash) readable in the fixture rather than \u-escaped.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [Fact]
    public void Trusted_scrubber_output_is_byte_exact_with_the_golden_snapshot()
    {
        if (System.Environment.GetEnvironmentVariable("PHI_GOLDEN_REGEN") == "1")
        {
            var regen = new List<GoldenEntry>();
            foreach (var input in Corpus)
            {
                regen.Add(new GoldenEntry
                {
                    Input = input,
                    Scrub = PhiScrubber.ScrubText(input),
                    Contains = PhiScrubber.ContainsPhi(input),
                });
            }
            File.WriteAllText(GoldenPath(), JsonSerializer.Serialize(regen, JsonOpts));
            return;
        }

        var golden = JsonSerializer.Deserialize<List<GoldenEntry>>(File.ReadAllText(GoldenPath()))!;
        Assert.Equal(Corpus.Length, golden.Count);

        for (var i = 0; i < golden.Count; i++)
        {
            var g = golden[i];
            Assert.Equal(Corpus[i], g.Input);
            Assert.Equal(g.Scrub, PhiScrubber.ScrubText(g.Input));
            Assert.Equal(g.Contains, PhiScrubber.ContainsPhi(g.Input));
        }
    }
}
