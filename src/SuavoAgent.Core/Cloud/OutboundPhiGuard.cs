using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Contracts.Models;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.State;
using SuavoAgent.Core.Workers;

namespace SuavoAgent.Core.Cloud;

internal static class OutboundPhiGuard
{
    private static readonly HashSet<string> BlockedFieldNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "rxnumber",
        "rx_number",
        "patientfirstname",
        "patientlastname",
        "patientlastinitial",
        "patientname",
        "patientphone",
        "deliveryaddress1",
        "deliveryaddress2",
        "deliverycity",
        "deliverystate",
        "deliveryzip",
        "firstname",
        "lastname",
        "lastinitial",
        "phone",
        "address1",
        "address2",
        "streetaddress",
        "dob",
        "dateofbirth",
        "ssn",
        "mrn",
        "insuranceid",
        "memberid",
        "policy",
        "rxdeliveryqueue",
    };

    public static void AssertAllowed(string path, string body, AgentOptions options)
    {
        using var doc = JsonDocument.Parse(body);
        var offendingField = FindPhiField(doc.RootElement, options.StrictOutboundTokenAllowlist);
        if (offendingField != null)
        {
            // PHI-safe diagnostic: name the FIELD that tripped the guard (never the
            // value). Without this, a blocked heartbeat is undebuggable — which is
            // exactly how a false-positive on legitimate telemetry can silently take
            // an agent offline. The field name flows into logs so the offending
            // payload field can be pinpointed and cleaned up.
            throw new InvalidOperationException(
                $"PHI-classified payload blocked before outbound cloud POST to {path} (field: {offendingField}).");
        }
    }

    /// <summary>
    /// Returns the normalized NAME of the first field whose name or value classifies
    /// as PHI, or null if the payload is clean. Returns the field name only — never
    /// the value — so it is safe to surface in exceptions and logs.
    /// </summary>
    private static string? FindPhiField(JsonElement element, bool strict, string? propertyName = null)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var normalized = NormalizeFieldName(property.Name);
                    if (BlockedFieldNames.Contains(normalized))
                        return normalized;
                    var nested = FindPhiField(property.Value, strict, normalized);
                    if (nested != null)
                        return nested;
                }

                return null;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    var nested = FindPhiField(item, strict, propertyName);
                    if (nested != null)
                        return nested;
                }

                return null;

            case JsonValueKind.String:
                var value = element.GetString();
                if (string.IsNullOrWhiteSpace(value))
                    return null;

                switch (ClassifyOutboundString(propertyName, value))
                {
                    case OutboundDecision.OperationalSafe:
                        return null;

                    // Geographic field (Safe-Harbor identifier) — allowed today, STRICT blocks it,
                    // SHADOW logs the would-block. Field name only; geo values match no enforced
                    // denylist rule so a value scan wouldn't help.
                    case OutboundDecision.GeographicExempt:
                        if (strict)
                            return propertyName ?? "(root)";
                        Serilog.Log.Warning(
                            "OutboundPhiGuard shadow: geographic field {Field} would be BLOCKED under "
                            + "StrictOutboundTokenAllowlist. No value logged — review before enabling strict.",
                            propertyName ?? "(root)");
                        return null;

                    // Charset-clean but NOT a non-PHI token shape (a packed identifier like
                    // "DOE-JOHN-1990", a bare DOB "1990-01-15", an SSN "123-45-6789", a 10-digit
                    // phone). ALWAYS run the enforced ContainsPhi value scan — the previous code
                    // let these bypass it and leak PHI under a benign field name (HIPAA). The strict
                    // token allow-list still applies ON TOP for non-PHI unrecognized tokens.
                    case OutboundDecision.UnrecognizedToken:
                        if (PhiScrubber.ContainsPhi(value))
                            return propertyName ?? "(root)";
                        if (strict)
                            return propertyName ?? "(root)";
                        Serilog.Log.Warning(
                            "OutboundPhiGuard shadow: field {Field} would be BLOCKED under "
                            + "StrictOutboundTokenAllowlist (not on the operational token allow-list). "
                            + "No value logged — review before enabling strict mode.",
                            propertyName ?? "(root)");
                        return null;

                    case OutboundDecision.NeedsDenylistScan:
                    default:
                        // Some telemetry ships pre-validated, pre-serialized JSON as a string
                        // value (intelligenceContext, efficiencyReport, fleetSignals). Parse and
                        // recurse so each leaf gets per-field treatment: embedded timestamps are
                        // exempted; embedded PHI is still caught and named by its nested field.
                        if (TryParseNestedJson(value, out var parsed))
                            return FindPhiField(parsed, strict, propertyName);

                        // EnforcedDenylist — EXACTLY today's Core PHI patterns, incl. the broad
                        // DatePattern (blueprint fix #3). A match blocks the outbound POST.
                        if (PhiScrubber.ContainsPhi(value))
                            return propertyName ?? "(root)";

                        // ShadowDenylist (blueprint fix #5) — the Diagnostics-origin staged rules
                        // (NDC / narrow DOB / Windows path / PioneerRx / checksum-DEA / member-id)
                        // are NOT enforced yet. Log the would-block so false-positives are measured
                        // on a real pilot before promotion. Field name + rule name only — never the
                        // value (parallels the charset-path shadow log above).
                        var shadowRule = PhiScrubber.ShadowDenylistMatch(value);
                        if (shadowRule is not null)
                        {
                            Serilog.Log.Warning(
                                "OutboundPhiGuard shadow-denylist: field {Field} would be BLOCKED by "
                                + "staged rule {Rule} (not yet enforced). No value logged — review "
                                + "before promotion.",
                                propertyName ?? "(root)", shadowRule);
                        }
                        return null;
                }

            default:
                return null;
        }
    }

    private static bool TryParseNestedJson(string value, out JsonElement element)
    {
        element = default;
        var trimmed = value.AsSpan().TrimStart();
        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
            return false;
        try
        {
            using var doc = JsonDocument.Parse(value);
            element = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // ISO-8601 datetimes (incl. UTC "+00:00"/"Z" offsets) are operational metadata,
    // not PHI. Requires the "T" + time, so a bare date like a "1990-01-15" DOB is NOT
    // matched and stays subject to the PHI scan.
    private static readonly Regex IsoTimestamp = new(
        @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:?\d{2})?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private enum OutboundDecision
    {
        /// <summary>Operational by field name or a recognized machine token shape — never PHI.</summary>
        OperationalSafe,
        /// <summary>Geographic field (city/state/zip5) — a HIPAA Safe-Harbor identifier.</summary>
        GeographicExempt,
        /// <summary>Passes the legacy &lt;=96-char charset but matches no known token — the closed hole.</summary>
        UnrecognizedToken,
        /// <summary>Free-form text — scan with the deny-list (and recurse into nested JSON).</summary>
        NeedsDenylistScan,
    }

    // Known OPERATIONAL token shapes (positive allow-list). Deliberately tight: bare single
    // words are NOT allowed by value (a surname would hide there) — legitimate enum values
    // arrive under operational field names (status/outcome/severity/classification/mode…) and
    // are exempted by NAME instead. Tune this via the shadow would-block logs before enabling
    // StrictOutboundTokenAllowlist.
    // NON-PHI token shapes ONLY — these skip the value scan because no PHI identifier can hide in
    // them. The hyphenated-NDC (\d{1,5}-\d{1,4}-\d{1,2}) and long-numeric (\d{6,}) shapes were
    // REMOVED: NDC collides with a bare DOB (1990-01-15 is 4-2-2) and long-numeric collides with
    // MRN / SSN-without-dashes / 10-digit phone. Anything charset-clean that isn't one of these
    // now falls to the enforced ContainsPhi value scan (see the UnrecognizedToken case).
    private static readonly Regex KnownOperationalToken = new(
        @"^(?:" +
        @"[0-9a-fA-F]{16,128}" +                                                 // hex hash / digest
        @"|[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}" + // uuid
        @"|v?\d+\.\d+(?:\.\d+)?(?:[-.][0-9A-Za-z]+)*" +                          // semver / version
        @"|[a-z][a-z0-9]*(?:_[a-z0-9]+)+" +                                      // snake_case enum (sql_first, rx_delivery_queue)
        @")$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static OutboundDecision ClassifyOutboundString(string? propertyName, string value)
    {
        // Hard-safe operational field NAMES (hash/digest/timestamps/machine ids/enums).
        if (propertyName is not null &&
            (propertyName.EndsWith("hash", StringComparison.OrdinalIgnoreCase) ||
             propertyName.EndsWith("sha256", StringComparison.OrdinalIgnoreCase) ||
             propertyName.Contains("digest", StringComparison.OrdinalIgnoreCase) ||
             propertyName.Contains("timestamp", StringComparison.OrdinalIgnoreCase) ||
             propertyName.Contains("capturedat", StringComparison.OrdinalIgnoreCase) ||
             propertyName.Contains("syncedat", StringComparison.OrdinalIgnoreCase) ||
             propertyName is "ndc" or "evidenceid" or "scanwindowid" or "sessionid" or "schemaversion" or "schemasignature" or
                 "writebackid" or "commandid" or "candidateid" or "pharmacyid" or "orderid" or "inboxitemid" or
                 "approvalid" or "ruleid" or "templateid" or "runid" or
                 "transition" or "transitionat" or "resultcode" or "completedat" or "idempotent" or
                 "pms" or "pmsversion" or "status" or "outcome" or "severity" or "source" or "sourcedetail" or
                 "classification" or "priority" or "temperaturerequirement"))
        {
            return OutboundDecision.OperationalSafe;
        }

        // Geographic field names — exempt today, but Safe-Harbor identifiers. Separated so
        // strict mode can block them (city + state + zip5 is a re-identification vector at a
        // small pharmacy) while shadow mode logs + allows.
        if (propertyName is "city" or "state" or "zip5")
            return OutboundDecision.GeographicExempt;

        // ISO-8601 datetimes (incl. "Z"/"+00:00" offsets) are operational metadata. Requires the
        // "T" + time, so a bare date like a "1990-01-15" DOB is NOT matched and stays scanned.
        if (IsoTimestamp.IsMatch(value))
            return OutboundDecision.OperationalSafe;

        // Strings with spaces/punctuation outside the safe charset are free-form → deny-list scan
        // (today's behavior, preserved for both modes).
        var charsetOk = value.Length <= 96 &&
                        value.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.' or ':');
        if (!charsetOk)
            return OutboundDecision.NeedsDenylistScan;

        // Charset-clean: SAFE only if it matches a known operational token shape. Anything else
        // (the old escape hatch — a packed identifier like "DOE-JOHN-1990") is unrecognized.
        return KnownOperationalToken.IsMatch(value)
            ? OutboundDecision.OperationalSafe
            : OutboundDecision.UnrecognizedToken;
    }

    private static string NormalizeFieldName(string name) =>
        new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
