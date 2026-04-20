namespace SuavoAgent.Contracts.Learning;

/// <summary>
/// Fingerprint of the PMS environment a template (or schema adaptation) was
/// authored against. Schema hash comes from
/// <see cref="SuavoAgent.Contracts.Canary.ContractFingerprinter"/>; the UIA
/// dialect hash is computed from the distinct AutomationId set observed for
/// the template's screen family.
///
/// Two installations are "compatible" under a template when the receiver's
/// own fingerprint appears in the template's <c>PmsVersionRange</c> by
/// equality on all three fields (<see cref="Matches"/>). PmsType comparison
/// is case-insensitive; hash comparisons are hex-lower exact.
///
/// ProductVersionString is audit-only (not used in matching) — it carries a
/// human-readable label like "PioneerRx 2026.3.1" for dashboard / log use.
/// </summary>
public sealed record PmsVersionFingerprint(
    string PmsType,
    string SchemaHash,
    string UiaDialectHash,
    string? ProductVersionString)
{
    public bool Matches(PmsVersionFingerprint other)
    {
        if (other is null) return false;
        if (!string.Equals(PmsType, other.PmsType, System.StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.Equals(SchemaHash, other.SchemaHash, System.StringComparison.Ordinal))
            return false;
        if (!string.Equals(UiaDialectHash, other.UiaDialectHash, System.StringComparison.Ordinal))
            return false;
        return true;
    }
}
