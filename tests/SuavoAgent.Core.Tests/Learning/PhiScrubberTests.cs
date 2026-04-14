// tests/SuavoAgent.Core.Tests/Learning/PhiScrubberTests.cs
using SuavoAgent.Core.Learning;
using Xunit;

namespace SuavoAgent.Core.Tests.Learning;

public class PhiScrubberTests
{
    [Theory]
    [InlineData("John Smith - Prescription", "[REDACTED] - Prescription")]
    [InlineData("Patient: Jane Doe", "Patient: [REDACTED]")]
    [InlineData("RX for 555-123-4567", "RX for [REDACTED]")]
    [InlineData("DOB: 01/15/1990", "DOB: [REDACTED]")]
    [InlineData("SSN 123-45-6789", "SSN [REDACTED]")]
    [InlineData("MRN: ABC12345", "MRN: [REDACTED]")]
    [InlineData("Point of Sale", "Point of Sale")]  // no PHI
    [InlineData("PioneerRx - Pharmacy Management", "PioneerRx - Pharmacy Management")]  // no PHI
    public void ScrubText_RemovesPhi(string input, string expected)
    {
        var result = PhiScrubber.ScrubText(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ScrubText_Null_ReturnsNull()
    {
        Assert.Null(PhiScrubber.ScrubText(null));
    }

    [Fact]
    public void HmacHash_DeterministicWithSameSalt()
    {
        var salt = "test-pharmacy-salt";
        var hash1 = PhiScrubber.HmacHash("patient-123", salt);
        var hash2 = PhiScrubber.HmacHash("patient-123", salt);
        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void HmacHash_DifferentWithDifferentSalt()
    {
        var hash1 = PhiScrubber.HmacHash("patient-123", "salt-a");
        var hash2 = PhiScrubber.HmacHash("patient-123", "salt-b");
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void ContainsPhi_DetectsPhoneNumbers()
    {
        Assert.True(PhiScrubber.ContainsPhi("Call 555-123-4567"));
        Assert.True(PhiScrubber.ContainsPhi("(555) 123-4567"));
        Assert.False(PhiScrubber.ContainsPhi("Port 12345"));
    }

    [Fact]
    public void ContainsPhi_DetectsSSN()
    {
        Assert.True(PhiScrubber.ContainsPhi("SSN: 123-45-6789"));
        Assert.False(PhiScrubber.ContainsPhi("ID: 12345"));
    }

    [Fact]
    public void ContainsPhi_DetectsDates()
    {
        Assert.True(PhiScrubber.ContainsPhi("DOB: 01/15/1990"));
        Assert.True(PhiScrubber.ContainsPhi("Born 1990-01-15"));
        Assert.False(PhiScrubber.ContainsPhi("Version 2.0.0"));
    }

    // -----------------------------------------------------------------------
    // Contextual name pattern tests
    // -----------------------------------------------------------------------

    [Fact]
    public void ScrubText_InlineName_RxContext_Scrubbed()
    {
        // "Rx: John Smith" — Rx is not covered by NameContextPattern, so ContextualNamePattern catches it
        var result = PhiScrubber.ScrubText("Rx: John Smith | Status: Ready");
        Assert.DoesNotContain("John Smith", result);
        Assert.Contains("[NAME]", result);
    }

    [Fact]
    public void ScrubText_InlineName_AddressContext_Scrubbed()
    {
        // Address is not in NameContextPattern's keyword list
        var result = PhiScrubber.ScrubText("Address: John Williams");
        Assert.DoesNotContain("John Williams", result);
        Assert.Contains("[NAME]", result);
    }

    [Fact]
    public void ScrubText_NoContext_NamePreserved()
    {
        var result = PhiScrubber.ScrubText("Point Sale — Status Type");
        Assert.DoesNotContain("[NAME]", result);
    }

    [Fact]
    public void ScrubText_PioneerRx_NotFalsePositive()
    {
        // "PioneerRx" should NOT trigger contextual pattern — Rx is inside a compound word
        var result = PhiScrubber.ScrubText("PioneerRx - Pharmacy Management");
        Assert.Equal("PioneerRx - Pharmacy Management", result);
    }

    [Theory]
    [InlineData("Rx: Jane Doe | ID: 999")]
    [InlineData("Phone: 555-1234 Address: John Williams")]
    public void ScrubText_ContextualNames_AllScrubbed(string input)
    {
        var result = PhiScrubber.ScrubText(input);
        Assert.Contains("[NAME]", result);
    }
}
