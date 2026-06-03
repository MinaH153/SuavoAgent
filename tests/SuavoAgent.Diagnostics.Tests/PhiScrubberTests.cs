using SuavoAgent.Diagnostics;
using Xunit;

namespace SuavoAgent.Diagnostics.Tests;

/// PHI scrubber corpus — 50+ patterns per spec §3 + the 13 pharmacy-specific
/// patterns Codex re-review added (§8.2 RESOLVED v1.0). CI fails if any
/// test PHI pattern reaches the post-scrub event.
public class PhiScrubberTests
{
    private static PhiScrubber Scrubber()
    {
        var ruleset = new RulesetV1();
        // 200ms timeout — production default is 10ms (spec §4 contract).
        // Tests need headroom for cold JIT on Linux CI runner where the
        // regex compile + first-match for 13 pharmacy patterns can
        // exceed 50ms on the first invocation.
        return new PhiScrubber(ruleset, TimeSpan.FromMilliseconds(200));
    }

    // ── PioneerRx-specific identifiers ──

    [Theory]
    [InlineData("PatientID")]
    [InlineData("RxNumber")]
    [InlineData("PrescriberID")]
    [InlineData("PharmacyChainID")]
    public void PioneerRx_JSON_identifiers_are_redacted(string field)
    {
        var input = $"event payload: \"{field}\":\"P12345\" arrived at 12:00";
        var output = Scrubber().Sanitize(input);
        Assert.DoesNotContain("P12345", output);
        Assert.Contains("[PIONEERRX_ID]", output);
    }

    [Theory]
    [InlineData("RxNumber")]
    [InlineData("PatientID")]
    public void PioneerRx_XML_identifiers_are_redacted(string field)
    {
        var input = $"<root><{field}>RX1234567</{field}></root>";
        var output = Scrubber().Sanitize(input);
        Assert.DoesNotContain("RX1234567", output);
        Assert.Contains("[PIONEERRX_ID]", output);
    }

    [Theory]
    [InlineData("PatientID")]
    [InlineData("RxNumber")]
    public void PioneerRx_SQL_literals_are_redacted(string field)
    {
        var input = $"SELECT * FROM rx WHERE {field}='P12345' AND status='active'";
        var output = Scrubber().Sanitize(input);
        Assert.DoesNotContain("P12345", output);
        Assert.Contains("[PIONEERRX_ID]", output);
    }

    // ── NDC formats (4 hyphenations + unhyphenated-with-label) ──

    [Theory]
    [InlineData("ndc=1234-5678-90", "[NDC]")]            // 4-4-2
    [InlineData("12345-6789-01 dispensed", "[NDC]")]     // 5-4-2
    [InlineData("drug 12345-678-90 ok", "[NDC]")]        // 5-3-2
    [InlineData("pkg 12345-6789-0", "[NDC]")]            // 5-4-1
    public void NDC_hyphenated_variants_redacted(string input, string redaction)
    {
        var output = Scrubber().Sanitize(input);
        Assert.Contains(redaction, output);
    }

    [Theory]
    [InlineData("\"NDC\":\"12345678901\"")]
    [InlineData("ndc=12345678901")]
    [InlineData("National_Drug_Code: 12345678901")]
    public void NDC_unhyphenated_with_label_redacted(string input)
    {
        var output = Scrubber().Sanitize(input);
        Assert.DoesNotContain("12345678901", output);
        Assert.Contains("[NDC]", output);
    }

    // ── DEA numbers (shape + checksum validation) ──

    [Fact]
    public void DEA_with_valid_checksum_redacts()
    {
        // AB1234563: digits = 1,2,3,4,5,6,3
        // checksum = (1+3+5) + 2*(2+4+6) = 9 + 24 = 33; 33 % 10 = 3 ✓
        // The PhiScrubber treats this regex as a shape match regardless
        // of checksum (the regex-only redaction is the safety net).
        var input = "prescriber DEA AB1234563 wrote";
        var output = Scrubber().Sanitize(input);
        Assert.Contains("[DEA]", output);
    }

    [Theory]
    [InlineData("AB1234563", true)]  // valid checksum
    [InlineData("XY7654321", false)] // bogus
    public void DEA_checksum_validator(string candidate, bool expected)
    {
        Assert.Equal(expected, PhiScrubber.DeaChecksumValid(candidate));
    }

    // ── NPI in field context ──

    [Theory]
    [InlineData("PrescriberNPI=1234567893")]
    [InlineData("provider_npi: 1234567893")]
    [InlineData("\"npi\":\"1234567893\"")]
    public void NPI_field_context_redacted(string input)
    {
        var output = Scrubber().Sanitize(input);
        Assert.DoesNotContain("1234567893", output);
        Assert.Contains("[NPI]", output);
    }

    // ── Insurance member IDs (field-context required) ──

    [Theory]
    [InlineData("BCBS member ABC123456789 dispensed")]
    [InlineData("blue cross MEM123456 OK")]
    public void BCBS_member_id_redacted(string input)
    {
        var output = Scrubber().Sanitize(input);
        Assert.Contains("[MEMBER_ID]", output);
    }

    [Fact]
    public void Aetna_member_id_redacted()
    {
        var input = "Aetna ID W123456789 valid";
        var output = Scrubber().Sanitize(input);
        Assert.Contains("[MEMBER_ID]", output);
    }

    [Fact]
    public void Cigna_member_id_redacted()
    {
        var input = "Cigna member U123456789 valid";
        var output = Scrubber().Sanitize(input);
        Assert.Contains("[MEMBER_ID]", output);
    }

    // ── Standard PHI shapes ──

    [Fact]
    public void SSN_is_redacted()
    {
        var input = "patient ssn 123-45-6789 confirmed";
        var output = Scrubber().Sanitize(input);
        Assert.DoesNotContain("123-45-6789", output);
        Assert.Contains("[SSN]", output);
    }

    [Theory]
    [InlineData("01/15/1985")]
    [InlineData("1/15/1985")]
    [InlineData("12-31-2024")]
    [InlineData("12/31/2024")]
    public void DOB_shapes_are_redacted(string dob)
    {
        var input = $"patient DOB {dob} reviewed";
        var output = Scrubber().Sanitize(input);
        Assert.DoesNotContain(dob, output);
        Assert.Contains("[DOB]", output);
    }

    [Theory]
    [InlineData("RX1234567")]
    [InlineData("rx987654321")]
    [InlineData("RX12345678901")]
    public void Rx_number_PioneerRx_pattern_redacted(string rx)
    {
        var input = $"prescription {rx} filled";
        var output = Scrubber().Sanitize(input);
        Assert.DoesNotContain(rx, output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[RX_NUM]", output);
    }

    [Theory]
    [InlineData("C:\\Users\\joshua\\Code\\SuavoAgent\\src\\file.cs")]
    [InlineData("/Users/joshua/Documents/data.json")]
    public void Windows_user_path_redacted(string path)
    {
        var output = Scrubber().Sanitize(path);
        Assert.DoesNotContain("joshua", output);
        Assert.Contains("[USER]", output);
    }

    // ── Patient names dictionary stays empty in Phase 1 ──

    [Fact]
    public void Patient_names_seed_is_empty_in_Phase1()
    {
        // RulesetV1 default is empty; spec §8.2 RESOLVED v1.0 mandates this.
        var ruleset = new RulesetV1();
        Assert.Empty(ruleset.PatientNamesSeed);
    }

    [Fact]
    public void Common_words_are_NOT_redacted_when_seed_is_empty()
    {
        // Codex's concern: Census/global dictionaries would redact "May",
        // "Will", "King", etc. Phase 1 empty seed → no false positives.
        var input = "The May release Will ship in King City after Brown gives Price approval.";
        var output = Scrubber().Sanitize(input);
        Assert.Contains("May", output);
        Assert.Contains("Will", output);
        Assert.Contains("King", output);
        Assert.Contains("Brown", output);
        Assert.Contains("Price", output);
    }

    // ── Null / empty / whitespace handling ──

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Empty_and_null_inputs_return_empty_string(string? input)
    {
        Assert.Equal(string.Empty, Scrubber().Sanitize(input));
    }

    [Fact]
    public void Plain_text_without_PHI_is_unchanged()
    {
        var input = "Workflow completed successfully with no PHI present in this string.";
        var output = Scrubber().Sanitize(input);
        Assert.Equal(input, output);
    }

    // ── Catastrophic-backtracking defense (NonBacktracking option) ──

    [Fact]
    public void Catastrophic_input_does_not_hang()
    {
        // Classic "a"*100 + "!" backtracking case. NonBacktracking
        // guarantees linear time.
        var input = new string('a', 5000) + "!";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var output = Scrubber().Sanitize(input);
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 500,
            $"Scrubber took {sw.ElapsedMilliseconds}ms on catastrophic input (must be <500ms)");
    }
}
