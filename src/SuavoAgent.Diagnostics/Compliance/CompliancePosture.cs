namespace SuavoAgent.Core.Compliance;

/// <summary>
/// Ranked compliance modes. Higher ordinal = stricter.
/// Fail-closed default = Hipaa (unknown/absent verticalConfig → Hipaa).
/// </summary>
public enum ComplianceMode
{
    None  = 0,  // "none"  — no regulated compliance requirements
    Hipaa = 1,  // "hipaa" — HIPAA BA required
    Pci   = 2,  // "pci"   — PCI-DSS
}

/// <summary>
/// Resolves + enforces compliance posture for vertical-config onboarding.
/// Fail-closed: unknown / absent / invalid → Hipaa.
/// Anti-downgrade: incoming.max(lkg) ensures the box never relaxes below
/// the last-known-good level without an explicit verified upgrade.
/// </summary>
public static class CompliancePosture
{
    /// <summary>
    /// Parse a complianceMode string from a <see cref="VerticalConfigDto"/>.
    /// Unknown/null/empty → <see cref="ComplianceMode.Hipaa"/> (fail-closed).
    /// </summary>
    public static ComplianceMode Resolve(string? complianceModeStr) =>
        complianceModeStr?.ToLowerInvariant() switch
        {
            "none"  => ComplianceMode.None,
            "hipaa" => ComplianceMode.Hipaa,
            "pci"   => ComplianceMode.Pci,
            _       => ComplianceMode.Hipaa,  // fail-closed
        };

    /// <summary>
    /// Enforce anti-downgrade: returns the stricter of <paramref name="incoming"/>
    /// and <paramref name="lastKnownGood"/>. A cloud push can only tighten, never relax.
    /// </summary>
    public static ComplianceMode Enforce(ComplianceMode incoming, ComplianceMode lastKnownGood) =>
        (ComplianceMode)Math.Max((int)incoming, (int)lastKnownGood);
}
