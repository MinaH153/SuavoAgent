using SuavoAgent.Core.Learning;
using Xunit;

namespace SuavoAgent.Core.Tests.Learning;

/// <summary>
/// Regression tests for street address detection in PhiScrubber
/// (Codex 2026-04-26 P1 finding). OCR output from PMS pharmacy windows
/// commonly includes patient mailing addresses; the previous scrubber
/// had no address pattern, so addresses crossed the IPC + cloud boundary.
/// </summary>
public class PhiScrubberAddressTests
{
    [Theory]
    [InlineData("Address: 1234 Maple Street, Apt 2")]
    [InlineData("Address: 1234 Maple Street Apt 2")]
    [InlineData("Address: 1234 Maple St")]
    [InlineData("Addr: 555 Main Street")]
    [InlineData("Mailing Address: 9876 Oak Avenue")]
    [InlineData("ADDRESS: 42 Elm Drive Suite 100")]
    public void ScrubText_AddressKeyword_Scrubbed(string input)
    {
        var result = PhiScrubber.ScrubText(input);
        Assert.NotNull(result);
        Assert.DoesNotContain("Maple", result);
        Assert.DoesNotContain("Main Street", result);
        Assert.DoesNotContain("Oak Avenue", result);
        Assert.DoesNotContain("Elm Drive", result);
        Assert.DoesNotContain("1234", result);
        Assert.DoesNotContain("555", result);
        Assert.DoesNotContain("9876", result);
        Assert.DoesNotContain("42 Elm", result);
    }

    [Theory]
    [InlineData("1234 Maple Street, Apt 2")]
    [InlineData("1234 Maple Street")]
    [InlineData("1234 Maple St.")]
    [InlineData("9876 Oak Avenue")]
    [InlineData("555 Main Boulevard")]
    [InlineData("42 Elm Drive Suite 100")]
    [InlineData("17 N Cherry Lane #4")]
    public void ScrubText_StreetSuffix_Scrubbed(string input)
    {
        // Standalone street addresses (no "Address:" keyword) are still PHI
        // and must scrub. Anchor: number + words + street-type suffix.
        var result = PhiScrubber.ScrubText(input);
        Assert.NotNull(result);
        Assert.DoesNotContain("Maple", result);
        Assert.DoesNotContain("Oak Avenue", result);
        Assert.DoesNotContain("Main Boulevard", result);
        Assert.DoesNotContain("Elm Drive", result);
        Assert.DoesNotContain("Cherry Lane", result);
    }

    [Theory]
    [InlineData("Page 1 of 5")]
    [InlineData("Zone 12 Active")]
    [InlineData("Version 2.0 Build 1234")]
    [InlineData("Window 3 Open")]
    [InlineData("Item 7 Quantity 12")]
    [InlineData("Tab 4 Selected")]
    [InlineData("Aisle 12 Bin 4")]
    public void ScrubText_NotAnAddress_NotScrubbed(string input)
    {
        // Bare digits + capitalized words must NOT trigger the address pattern
        // unless followed by a street-type suffix. False positives on UI labels
        // would break the whole vision pipeline.
        var result = PhiScrubber.ScrubText(input);
        Assert.Equal(input, result);
    }

    [Fact]
    public void ContainsPhi_DetectsAddress()
    {
        Assert.True(PhiScrubber.ContainsPhi("Address: 1234 Maple Street, Apt 2"));
        Assert.True(PhiScrubber.ContainsPhi("1234 Maple Street"));
        Assert.False(PhiScrubber.ContainsPhi("Page 12 of 50"));
    }

    [Fact]
    public void ScrubText_AddressInLargerString_OnlyAddressScrubbed()
    {
        var input = "Patient verified | Address: 1234 Maple Street | Status: Ready";
        var result = PhiScrubber.ScrubText(input);
        Assert.NotNull(result);
        Assert.DoesNotContain("1234 Maple Street", result);
        Assert.Contains("Status: Ready", result);
    }

    [Fact]
    public void ScrubText_PoBoxAddress_Scrubbed()
    {
        var inputs = new[]
        {
            "Address: PO Box 1234",
            "Address: P.O. Box 9876",
            "PO Box 1234, San Diego",
        };
        foreach (var input in inputs)
        {
            var result = PhiScrubber.ScrubText(input);
            Assert.DoesNotContain("1234", result);
            Assert.DoesNotContain("9876", result);
        }
    }

    // ── Codex review fixes (2026-04-27) ──

    [Theory]
    [InlineData("1234-A Main Street")]
    [InlineData("1234-B Oak Avenue")]
    [InlineData("Address: 1234-A Main Street, Apt 2")]
    public void ScrubText_HyphenatedCivicNumber_Scrubbed(string input)
    {
        // Codex CRITICAL: hyphenated civic numbers (1234-A) bypassed StreetSuffix,
        // then LastFirstPattern partially scrubbed the suffix while real address
        // body remained.
        var result = PhiScrubber.ScrubText(input);
        Assert.NotNull(result);
        Assert.DoesNotContain("Main Street", result);
        Assert.DoesNotContain("Oak Avenue", result);
        Assert.DoesNotContain("1234-A", result);
        Assert.DoesNotContain("1234-B", result);
    }

    [Theory]
    [InlineData("APO AE 09012")]
    [InlineData("FPO AP 96662")]
    [InlineData("DPO AA 34001")]
    public void ScrubText_MilitaryAddress_Scrubbed(string input)
    {
        // Codex CRITICAL: military APO/FPO/DPO addresses are HIPAA Safe Harbor
        // identifier #5 (geographic subdivision) and must scrub.
        var result = PhiScrubber.ScrubText(input);
        Assert.NotNull(result);
        Assert.DoesNotContain("09012", result);
        Assert.DoesNotContain("96662", result);
        Assert.DoesNotContain("34001", result);
    }

    [Theory]
    [InlineData("RR 1 Box 5")]
    [InlineData("HC 67 Box 23")]
    [InlineData("Address: RR 2 Box 99")]
    public void ScrubText_RuralRoute_Scrubbed(string input)
    {
        // Codex CRITICAL: rural-route addresses bypassed all 3 patterns.
        var result = PhiScrubber.ScrubText(input);
        Assert.NotNull(result);
        Assert.DoesNotContain("Box 5", result);
        Assert.DoesNotContain("Box 23", result);
        Assert.DoesNotContain("Box 99", result);
    }

    [Theory]
    [InlineData("P.O.B. 1234")]
    [InlineData("P O Box 1234")]
    [InlineData("PO BOX 9876")]
    [InlineData("p.o. box 5555")]
    public void ScrubText_PoBoxVariants_Scrubbed(string input)
    {
        // Codex CRITICAL: PO Box variants bypassed.
        var result = PhiScrubber.ScrubText(input);
        Assert.NotNull(result);
        Assert.DoesNotContain("1234", result);
        Assert.DoesNotContain("9876", result);
        Assert.DoesNotContain("5555", result);
    }

    [Theory]
    [InlineData("1234 5th Avenue")]
    [InlineData("567 42nd Street")]
    [InlineData("89 1st Boulevard")]
    [InlineData("100 3rd Court")]
    public void ScrubText_NumericStreetName_Scrubbed(string input)
    {
        // Codex HIGH: ordinal street names (5th Avenue, 42nd Street) must scrub.
        var result = PhiScrubber.ScrubText(input);
        Assert.NotNull(result);
        Assert.DoesNotContain("5th Avenue", result);
        Assert.DoesNotContain("42nd Street", result);
        Assert.DoesNotContain("1st Boulevard", result);
        Assert.DoesNotContain("3rd Court", result);
    }

    [Fact]
    public void ScrubText_AddressContext_DoesNotConsumeAdjacentField()
    {
        // Codex BLOCKER: AddressContextPattern consumed to end-of-line, wiping
        // adjacent fields like DOB. Must stop at the next field-keyword.
        var input = "Address: 1234 Maple Street DOB: 01/15/1990";
        var result = PhiScrubber.ScrubText(input);
        Assert.NotNull(result);
        Assert.DoesNotContain("1234 Maple Street", result);
        // The DOB keyword survives; the date may be separately scrubbed by DatePattern
        // but must not be absorbed into the address replacement.
        Assert.Contains("DOB:", result);
    }

    [Theory]
    [InlineData("12 Months Old Dr Smith")]      // Dr is a doctor abbreviation, not a Drive
    [InlineData("Lot Box 4567")]                 // Box <digits> is an inventory label
    [InlineData("30.5 N 120.3 W Way")]           // GPS coordinates with NSEW
    [InlineData("Last 3 Records Active")]
    public void ScrubText_FalsePositive_NotScrubbed(string input)
    {
        // Codex HIGH: 2-letter abbreviations and bare "Box <digits>" cause
        // false positives in PMS UI strings.
        var result = PhiScrubber.ScrubText(input);
        Assert.Equal(input, result);
    }

    [Fact]
    public void ScrubText_AptTail_DoesNotConsumeAdjacentIdentifier()
    {
        // Codex MEDIUM: unbounded apartment tail [\w\d-]+ greedily eats the
        // next identifier (e.g., PatientID 999).
        var input = "123 Maple St, Apt 5 PatientID 999";
        var result = PhiScrubber.ScrubText(input);
        Assert.NotNull(result);
        Assert.DoesNotContain("Maple", result);
        // PatientID label and the 999 identifier must remain — they are NOT
        // part of the apartment value and must not be absorbed.
        Assert.Contains("PatientID", result);
        Assert.Contains("999", result);
    }

    [Fact]
    public void ScrubText_AddressLabel_IsRedacted_NotAddress()
    {
        // Codex HIGH: downstream consumers expect [REDACTED] not [ADDRESS].
        // Keep the universal label for consistency across PHI categories.
        var input = "Address: 1234 Maple Street";
        var result = PhiScrubber.ScrubText(input);
        Assert.NotNull(result);
        Assert.DoesNotContain("[ADDRESS]", result);
        Assert.Contains("[REDACTED]", result);
    }
}
