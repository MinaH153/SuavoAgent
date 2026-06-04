using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace SuavoAgent.Diagnostics.Phi;

/// <summary>
/// The single source of truth for every PHI detection rule in the agent, expressed AS DATA
/// (pattern strings + per-rule options + engine + replacement + ordering). Two engines
/// materialize from this one catalog:
///
/// <list type="bullet">
/// <item><see cref="Trusted"/> → <c>PhiTextScrubber.ScrubText</c> / <c>ContainsPhi</c> — the
/// on-box, structured, full-fidelity Standard-engine path. Mirrors the legacy
/// <c>SuavoAgent.Core.Learning.PhiScrubber</c> exactly.</item>
/// <item><see cref="Untrusted"/> → <c>Diagnostics.PhiScrubber.Sanitize</c> — the hostile
/// crash-text → Sentry path. NonBacktracking-only (ReDoS-immune). Mirrors the legacy
/// <c>Diagnostics.PhiScrubber.BuildRules</c> exactly.</item>
/// </list>
///
/// <para><b>TrustScope.</b> Most rules live on exactly one surface. The two genuinely shared
/// shapes — SSN (<see cref="SsnShape"/>) and the DEA token (<see cref="DeaShape"/>) — are
/// <see cref="PhiTrustScope.Both"/>: a SINGLE pattern const is referenced from both lists, so
/// the regex can never drift between engines, even though each surface materializes it with its
/// own options / replacement / validator (the trusted DEA is shape-only → <c>[REDACTED]</c>;
/// the untrusted DEA is checksum-gated → <c>[DEA]</c> — blueprint fix #2).</para>
///
/// <para>Ordering is the list position (the legacy arrays were order-sensitive: address rules
/// before name rules on the trusted side; Rx-number before DEA-shape on the untrusted side).
/// The byte-exact characterization gate (<c>PhiTrustedCharacterizationTests</c> /
/// <c>PhiUntrustedCharacterizationTests</c>) fails the instant any of this diverges.</para>
/// </summary>
public static class PhiRuleCatalog
{
    // ── Shared shapes (TrustScope.Both): one definition, referenced by both surfaces. ──
    private const string SsnShape = @"\b\d{3}-\d{2}-\d{4}\b";
    private const string DeaShape = @"\b[A-Z]{2}\d{7}\b";

    // Trusted whole-match replacement is universal (downstream-consumer consistency).
    public const string RedactedLabel = "[REDACTED]";
    private const string NameLabel = "[NAME]";
    private const string CodeLabel = "[CODE]";

    /// <summary>
    /// TRUSTED ScrubText / ContainsPhi pipeline. Order + options + replacement copied
    /// one-for-one from the legacy Core PhiPatterns array.
    /// </summary>
    public static IReadOnlyList<TrustedRuleSpec> Trusted { get; } = new[]
    {
        // Address group first — keyword-anchored AddressContext consumes a full address
        // before name rules see a fragment. Most-specific (military/rural) ahead of the
        // lazy AddressContext lookahead.
        new TrustedRuleSpec("MilitaryAddress",
            @"\b(?:APO|FPO|DPO)\s+(?:AA|AE|AP)\s+\d{5}\b",
            RegexOptions.IgnoreCase, RedactedLabel, false),
        new TrustedRuleSpec("RuralRoute",
            @"\b(?:RR|HC)\s+\d+\s+Box\s+\d+\b",
            RegexOptions.IgnoreCase, RedactedLabel, false),
        new TrustedRuleSpec("AddressContext",
            @"(?<=\b(?:Mailing|Home|Work|Shipping|Billing|Patient|Primary)?\s*Addr(?:ess)?[:\s]+)((?:\d|P\.?\s*O\.?|APO|FPO|DPO|RR|HC).*?)(?=\s*(?:\||$|\r|\n|\b(?:DOB|SSN|Phone|MRN|Patient|Name|Status|ID|Email|Tel|City|State|Zip|Notes|Allergy|Insurance|Caregiver|Member|Subscriber|Group|Policy|Plan)\s*[:=]))",
            RegexOptions.IgnoreCase | RegexOptions.Multiline, RedactedLabel, false),
        new TrustedRuleSpec("StreetSuffix",
            @"\b\d{1,5}(?:-[A-Za-z]\d?)?\s+(?:[NSEW][NSEW]?\.?\s+)?(?:(?:\d{1,3}(?:st|nd|rd|th)|[A-Za-z][A-Za-z'-]+)\s+){1,4}(?:Street|Avenue|Boulevard|Highway|Parkway|Terrace|Circle|Drive|Place|Court|Road|Lane|Way|(?:St|Ave|Blvd|Hwy|Pkwy|Ter|Cir|Dr|Pl|Ct|Rd|Ln)(?:\.|(?=,|\s+(?:Apartment|Apt|Suite|Ste|Unit|#)\b)))(?:\s*,?\s*(?:Apartment|Apt|Suite|Ste|Unit|#)\.?\s*[A-Za-z0-9]{1,6})?",
            RegexOptions.IgnoreCase, RedactedLabel, false),
        new TrustedRuleSpec("PoBox",
            @"\bP\.?\s*O\.?\s*(?:Box|B)\.?\s+\d+\b",
            RegexOptions.IgnoreCase, RedactedLabel, false),
        new TrustedRuleSpec("InsuranceId",
            @"(?<=\b(?:Member|Subscriber|Group|Policy|Insurance|Plan|Beneficiary|Carrier)(?:\s*(?:ID|Number|#|No)\.?)?\s*[:=]\s*)[A-Z0-9-]{3,}",
            RegexOptions.IgnoreCase, RedactedLabel, false),

        new TrustedRuleSpec("Ssn", SsnShape, RegexOptions.None, RedactedLabel, false),
        new TrustedRuleSpec("Phone",
            @"\(?\d{3}\)?[-.\s]?\d{3}[-.\s]?\d{4}",
            RegexOptions.None, RedactedLabel, false),
        new TrustedRuleSpec("Date",
            @"\b\d{1,2}[/-]\d{1,2}[/-]\d{2,4}\b|\b\d{4}-\d{2}-\d{2}\b",
            RegexOptions.None, RedactedLabel, false),
        new TrustedRuleSpec("Mrn",
            @"(?<=MRN[:\s]+)\w+",
            RegexOptions.IgnoreCase, RedactedLabel, false),
        new TrustedRuleSpec("NameContext",
            @"(?<=(?:Patient|Name|DOB|SSN)[:\s]+)(?!Address[:\s])\S+(?:\s+\S+)?",
            RegexOptions.IgnoreCase, RedactedLabel, false),
        new TrustedRuleSpec("LeadingName",
            @"^[A-Z][a-z]+\s+[A-Z][a-z]+(?=\s+-)",
            RegexOptions.None, RedactedLabel, false),
        new TrustedRuleSpec("Npi",
            @"\bNPI[:\s#]?\s*\d{10}\b",
            RegexOptions.IgnoreCase, RedactedLabel, false),
        // Trusted DEA: shape-only, NO checksum validator, so checksum-invalid tokens still
        // redact (fix #2). The checksum-gated DEA lives only on the Untrusted surface.
        new TrustedRuleSpec("DeaShape", DeaShape, RegexOptions.None, RedactedLabel, false),
        new TrustedRuleSpec("RxNumber",
            @"\bRx\s*[#Nn]o?[:\s.]+\d{4,10}\b",
            RegexOptions.IgnoreCase, RedactedLabel, false),

        // Group-1 rewrites: keep the structural keyword, redact only the captured value.
        new TrustedRuleSpec("ContextualName",
            @"\b(?:Patient|Name|Rx|DOB|SSN|Phone|Address)\s*[:=\-|]\s*([A-Z][a-z]+\s+[A-Z][a-z]+)",
            RegexOptions.None, NameLabel, true),
        new TrustedRuleSpec("LastFirst",
            @"\b([A-Z][a-z]+,\s+[A-Z][a-z]+(?:\s+[A-Z]\.?)?)\b",
            RegexOptions.None, NameLabel, true),
        new TrustedRuleSpec("AllCapsNameContext",
            @"(?<=\b(?:Patient|Name|DOB|Caregiver)[:\s=\-|]+)([A-Z]{2,}(?:[\s,]+[A-Z]{2,}(?:\s+[A-Z]\.?)?)+)\b",
            RegexOptions.None, NameLabel, true),
        new TrustedRuleSpec("IcdContext",
            @"(?<=\b(?:Dx|Diagnosis|ICD|Code)[:\s=]+)([A-TV-Z][0-9][0-9AB](?:\.[0-9A-TV-Z]{1,4})?)\b",
            RegexOptions.IgnoreCase, CodeLabel, true),
    };

    /// <summary>
    /// UNTRUSTED Sanitize pipeline. Order + options + replacement + validator copied
    /// one-for-one from the legacy Diagnostics BuildRules list. Every rule is
    /// NonBacktracking except PioneerRx-XML (backreference → Standard + timeout).
    /// </summary>
    public static IReadOnlyList<UntrustedRuleSpec> Untrusted { get; } = new[]
    {
        // ── PioneerRx field-context (highest selectivity, run first) ──
        new UntrustedRuleSpec("PioneerRx-JSON",
            @"""(RxNumber|PatientID|PrescriberID|PharmacyChainID)""\s*:\s*(?:""[^""]*""|[^,}\s]+)",
            RegexOptions.IgnoreCase, PhiEngineClass.NonBacktracking,
            @"""$1"":""[PIONEERRX_ID]""", true, PhiPostValidator.None),
        new UntrustedRuleSpec("PioneerRx-XML",
            @"<(RxNumber|PatientID|PrescriberID|PharmacyChainID)>[^<]+</\1>",
            RegexOptions.IgnoreCase, PhiEngineClass.Standard,
            @"<$1>[PIONEERRX_ID]</$1>", true, PhiPostValidator.None),
        new UntrustedRuleSpec("PioneerRx-SQL",
            @"\b(RxNumber|PatientID|PrescriberID|PharmacyChainID)\s*=\s*'[^']+'",
            RegexOptions.IgnoreCase, PhiEngineClass.NonBacktracking,
            @"$1='[PIONEERRX_ID]'", true, PhiPostValidator.None),

        // ── Insurance member IDs (field-context required) ──
        new UntrustedRuleSpec("BCBS-Member",
            @"\b(bcbs|blue\s*cross|member_?id)\b.{0,24}?\b[A-Z]{3}\d{6,14}\b",
            RegexOptions.IgnoreCase, PhiEngineClass.NonBacktracking,
            "$1 [MEMBER_ID]", false, PhiPostValidator.None),
        new UntrustedRuleSpec("Aetna-Member",
            @"\b(aetna|member_?id)\b.{0,24}?\bW\d{8,12}\b",
            RegexOptions.IgnoreCase, PhiEngineClass.NonBacktracking,
            "$1 [MEMBER_ID]", false, PhiPostValidator.None),
        new UntrustedRuleSpec("Cigna-Member",
            @"\b(cigna|member_?id)\b.{0,24}?\bU?\d{9,12}\b",
            RegexOptions.IgnoreCase, PhiEngineClass.NonBacktracking,
            "$1 [MEMBER_ID]", false, PhiPostValidator.None),

        // ── Prescriber NPI (field-context required) ──
        new UntrustedRuleSpec("Prescriber-NPI-field",
            @"\b(prescriber_?npi|provider_?npi|npi)\s*""?\s*[:=]\s*""?\d{10}""?",
            RegexOptions.IgnoreCase, PhiEngineClass.NonBacktracking,
            "$1=[NPI]", true, PhiPostValidator.None),

        // ── NDC variants (5 hyphenation formats) ──
        new UntrustedRuleSpec("NDC-4-4-2",
            @"\b\d{4}-\d{4}-\d{2}\b",
            RegexOptions.IgnoreCase, PhiEngineClass.NonBacktracking,
            "[NDC]", false, PhiPostValidator.None),
        new UntrustedRuleSpec("NDC-5-4-2",
            @"\b\d{5}-\d{4}-\d{2}\b",
            RegexOptions.IgnoreCase, PhiEngineClass.NonBacktracking,
            "[NDC]", false, PhiPostValidator.None),
        new UntrustedRuleSpec("NDC-5-3-2",
            @"\b\d{5}-\d{3}-\d{2}\b",
            RegexOptions.IgnoreCase, PhiEngineClass.NonBacktracking,
            "[NDC]", false, PhiPostValidator.None),
        new UntrustedRuleSpec("NDC-5-4-1",
            @"\b\d{5}-\d{4}-\d\b",
            RegexOptions.IgnoreCase, PhiEngineClass.NonBacktracking,
            "[NDC]", false, PhiPostValidator.None),
        new UntrustedRuleSpec("NDC-unhyphenated-with-label",
            @"\b(ndc|national[_\s-]?drug[_\s-]?code)\s*""?\s*[:=]\s*""?\d{10,11}""?",
            RegexOptions.IgnoreCase, PhiEngineClass.NonBacktracking,
            "$1=[NDC]", false, PhiPostValidator.None),

        // ── Rx-number — MUST run BEFORE DEA shape (RX1234567 is 9 chars, both shapes). ──
        new UntrustedRuleSpec("Rx-number-PioneerRx",
            @"\bRX\d{6,12}\b",
            RegexOptions.IgnoreCase, PhiEngineClass.NonBacktracking,
            "[RX_NUM]", true, PhiPostValidator.None),

        // ── DEA (shape + checksum). Shares DeaShape with the trusted surface. ──
        new UntrustedRuleSpec("DEA-number-shape",
            DeaShape,
            RegexOptions.IgnoreCase, PhiEngineClass.NonBacktracking,
            "[DEA]", true, PhiPostValidator.DeaChecksum),

        // ── Standard PHI shapes ──
        new UntrustedRuleSpec("SSN",
            SsnShape,
            RegexOptions.IgnoreCase, PhiEngineClass.NonBacktracking,
            "[SSN]", true, PhiPostValidator.None),
        new UntrustedRuleSpec("DOB-shape",
            @"\b(0?[1-9]|1[0-2])[-/](0?[1-9]|[12]\d|3[01])[-/](19|20)\d{2}\b",
            RegexOptions.IgnoreCase, PhiEngineClass.NonBacktracking,
            "[DOB]", false, PhiPostValidator.None),

        // ── File path scrub (PII via username in path) ──
        new UntrustedRuleSpec("Windows-user-path",
            @"[\\/]Users[\\/][^\\/\s""'<>:|?*]+",
            RegexOptions.IgnoreCase, PhiEngineClass.NonBacktracking,
            "/Users/[USER]", false, PhiPostValidator.None),
    };

    /// <summary>
    /// Catalog-derived sentinel label tokens (without the surrounding brackets) used by
    /// <c>IsDefinitelyPhi</c> to strip a scrubber's own output before checking for residual
    /// PHI (blueprint fix #6). Covers every label emitted by either surface today PLUS the
    /// labels the cross-pollination work will emit, so the residual check never re-flags a
    /// correctly-scrubbed value. Single source: changing a label here updates the strip set.
    /// </summary>
    public static IReadOnlyList<string> SentinelLabels { get; } = new[]
    {
        // emitted by the untrusted surface today
        "PIONEERRX_ID", "MEMBER_ID", "NPI", "NDC", "DEA", "SSN", "RX_NUM", "USER", "DOB",
        // operational sentinels (PatientNamesSeed loop + fail-closed paths)
        "PATIENT", "SCRUB_TIMEOUT", "SCRUB_FAILED",
        // anticipated typed labels (cross-pollination of trusted shapes onto the NB engine)
        "PHONE", "MRN", "ADDRESS", "NAME", "CODE",
    };
}
