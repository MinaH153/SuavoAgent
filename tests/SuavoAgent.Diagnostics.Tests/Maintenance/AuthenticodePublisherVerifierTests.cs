using SuavoAgent.Diagnostics.Maintenance;
using Xunit;

namespace SuavoAgent.Diagnostics.Tests.Maintenance;

public sealed class AuthenticodePublisherVerifierTests
{
    private const string First =
        "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
    private const string Second =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [Fact]
    public void Expected_publisher_matches_active_ev_certificate_exactly()
    {
        Assert.Equal(
            "MKM TECHNOLOGIES LLC",
            AuthenticodePublisherVerifier.ExpectedPublisher);
    }

    [Fact]
    public void Exact_sha256_allowlist_normalizes_case_and_delimiters()
    {
        Assert.True(AuthenticodePublisherVerifier.TryParseSignerAllowlist(
            First.ToLowerInvariant() + "; " + Second,
            out var parsed));
        Assert.Equal(2, parsed.Count);
        Assert.Contains(First, parsed);
        Assert.Contains(Second, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0123456789ABCDEF")]
    [InlineData("GG23456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF")]
    public void Missing_or_non_sha256_allowlist_fails_closed(string? value)
    {
        Assert.False(AuthenticodePublisherVerifier.TryParseSignerAllowlist(
            value,
            out var parsed));
        Assert.Empty(parsed);
    }

    [Fact]
    public void Duplicate_or_empty_entries_are_rejected()
    {
        Assert.False(AuthenticodePublisherVerifier.TryParseSignerAllowlist(
            First + "," + First,
            out _));
        Assert.False(AuthenticodePublisherVerifier.TryParseSignerAllowlist(
            First + ",",
            out _));
    }

    [Fact]
    public void Local_build_without_embedded_release_allowlist_fails_before_file_trust()
    {
        var result = AuthenticodePublisherVerifier.Verify(
            Path.Combine(Path.GetTempPath(), "not-a-release.exe"));

        Assert.False(result.IsTrusted);
        Assert.Equal("authenticode_signer_allowlist_missing_or_invalid", result.Code);
    }
}
