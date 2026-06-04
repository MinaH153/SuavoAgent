using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Learning;

namespace SuavoAgent.Core.Cloud;

public interface IPostSigner
{
    Task<JsonElement?> PostSignedAsync(string path, object payload, CancellationToken ct);

    /// <summary>
    /// Like PostSignedAsync but also verifies the response body's ECDSA signature (H-11).
    /// Returns null if the response is unsigned or signature verification fails.
    /// </summary>
    Task<JsonElement?> PostSignedVerifiedAsync(string path, object payload, string publicKeyDer, CancellationToken ct);
}

public sealed class SuavoCloudClient : IPostSigner, IDisposable
{
    private readonly HttpClient _http;
    private readonly HmacSigner _signer;
    private readonly AgentOptions _options;

    public SuavoCloudClient(AgentOptions options)
        : this(options, CreateHandler(options))
    {
    }

    internal SuavoCloudClient(AgentOptions options, HttpMessageHandler handler)
    {
        _options = options;
        _signer = new HmacSigner(options.ApiKey ?? throw new InvalidOperationException("ApiKey is required"));

        var uri = new Uri(options.CloudUrl);
        if (uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException($"CloudUrl must use HTTPS, got: {uri.Scheme}");

        _http = new HttpClient(handler) { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(30) };
    }

    private static HttpMessageHandler CreateHandler(AgentOptions options)
    {
        var handler = new HttpClientHandler();
        if (!string.IsNullOrEmpty(options.CloudCertPin))
        {
            handler.ServerCertificateCustomValidationCallback = (message, cert, chain, errors) =>
            {
                if (errors != System.Net.Security.SslPolicyErrors.None) return false;
                if (cert == null) return false;
                var certHash = Convert.ToBase64String(
                    System.Security.Cryptography.SHA256.HashData(cert.GetPublicKey()));
                var pins = options.CloudCertPin!.Split(';', StringSplitOptions.RemoveEmptyEntries);
                return pins.Any(pin => pin.Equals(certHash, StringComparison.Ordinal));
            };
        }
        return handler;
    }

    public async Task<JsonElement?> HeartbeatAsync(object payload, CancellationToken ct)
    {
        return await PostSignedAsync("/api/agent/heartbeat", payload, ct);
    }

    public async Task<JsonElement?> SyncRxAsync(object payload, CancellationToken ct)
    {
        return await PostSignedAsync("/api/agent/sync", payload, ct);
    }

    /// <summary>
    /// Ships PHI to /api/agent/patient-details — driver-needed delivery
    /// fields only. The <see cref="SuavoAgent.Contracts.Models.PatientDetailsPayload"/>
    /// type is the deliberate compile-time contract: any new PHI field that
    /// reaches cloud has to land in that record first, which makes the diff
    /// impossible to miss in code review (Codex 2026-04-26 hardening).
    ///
    /// The Rx number itself is NEVER sent in cleartext alongside the hash;
    /// only <c>rxNumberHash</c> ships, and the payload record deliberately
    /// omits a RxNumber field.
    /// </summary>
    /// <returns><c>true</c> if PHI was dispatched; <c>false</c> if egress is
    /// gated off (fail-closed) and nothing left the box.</returns>
    public async Task<bool> SendPatientDetailsAsync(
        string rxNumber,
        SuavoAgent.Contracts.Models.PatientDetailsPayload details,
        string commandId,
        CancellationToken ct)
    {
        // Precedence-1 fail-closed gate (2026-06-04). The cloud
        // /api/agent/patient-details route does not exist in the canonical tree
        // and OutboundPhiGuard exempts the path, so an unguarded POST shipped
        // unscrubbed patient PHI to a 404 (edge/proxy logs). Do NOT touch the
        // wire until the audited route + typed contract + phi_egress_audit exist
        // (plan Stage A1). Log is PHI-safe: no patient fields, no Rx number.
        if (!_options.EnableAuditedPatientDetailsEgress)
        {
            Serilog.Log.Warning(
                "patient-details egress is fail-closed (EnableAuditedPatientDetailsEgress=false); "
                + "audited cloud route not yet built — patient PHI NOT sent (commandId {CommandId}).",
                commandId);
            return false;
        }

        var rxNumberHash = Learning.PhiScrubber.HmacHash(rxNumber, _options.HmacSalt ?? "[no-hmac-salt]");
        await PostSignedAsync("/api/agent/patient-details", new { rxNumberHash, details, commandId }, ct);
        return true;
    }

    public async Task<JsonElement?> PostSignedAsync(string path, object payload, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(payload);
        OutboundPhiGuard.AssertAllowed(path, body, _options);
        var timestamp = DateTimeOffset.UtcNow.ToString("o");
        var signature = _signer.Sign(timestamp, body);

        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        request.Headers.Add("x-agent-api-key", _options.ApiKey);
        request.Headers.Add("x-agent-timestamp", timestamp);
        request.Headers.Add("x-agent-signature", signature);

        using var response = await _http.SendAsync(request, ct);
        await EnsureCloudSuccessAsync(response, path, ct).ConfigureAwait(false);

        var responseBody = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(responseBody))
            return null;

        return JsonSerializer.Deserialize<JsonElement>(responseBody);
    }

    public async Task<JsonElement?> PostSignedVerifiedAsync(string path, object payload, string publicKeyDer, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(payload);
        OutboundPhiGuard.AssertAllowed(path, body, _options);
        var timestamp = DateTimeOffset.UtcNow.ToString("o");
        var signature = _signer.Sign(timestamp, body);

        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        request.Headers.Add("x-agent-api-key", _options.ApiKey);
        request.Headers.Add("x-agent-timestamp", timestamp);
        request.Headers.Add("x-agent-signature", signature);

        using var response = await _http.SendAsync(request, ct);
        await EnsureCloudSuccessAsync(response, path, ct).ConfigureAwait(false);

        var responseBody = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(responseBody))
            return null;

        // H-11: Reject seed responses with missing or invalid ECDSA signature.
        if (!response.Headers.TryGetValues("X-Response-Signature", out var sigValues)
            || !VerifyEcdsaSignature(responseBody, sigValues.FirstOrDefault() ?? "", publicKeyDer))
        {
            Serilog.Log.Warning("Seed response ECDSA signature missing or invalid — rejecting (H-11)");
            return null;
        }

        return JsonSerializer.Deserialize<JsonElement>(responseBody);
    }

    private static async Task EnsureCloudSuccessAsync(
        HttpResponseMessage response,
        string path,
        CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = response.Content == null
            ? null
            : await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var safeReason = CloudErrorSanitizer.FromBody(body);
        throw new HttpRequestException(
            $"Cloud request {path} failed with {(int)response.StatusCode} ({response.ReasonPhrase}); reason={safeReason}",
            null,
            response.StatusCode);
    }

    private static bool VerifyEcdsaSignature(string body, string signatureBase64, string publicKeyDer)
    {
        if (string.IsNullOrEmpty(signatureBase64)) return false;
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyDer), out _);
            var sigBytes = Convert.FromBase64String(signatureBase64);
            return ecdsa.VerifyData(Encoding.UTF8.GetBytes(body), sigBytes, HashAlgorithmName.SHA256);
        }
        catch { return false; }
    }

    /// <summary>
    /// Acknowledges execution of a signed cloud command. Updates agent_commands row
    /// with status=executed or failed, plus optional result/error.
    /// </summary>
    public async Task AckCommandAsync(string commandId, bool success, object? result, string? error, CancellationToken ct)
    {
        try
        {
            await PostSignedAsync(
                $"/api/agent/commands/{commandId}/ack",
                new
                {
                    status = success ? "executed" : "failed",
                    result,
                    error,
                },
                ct);
        }
        catch (Exception ex)
        {
            // Best-effort ack — don't crash the agent if cloud is unreachable.
            Serilog.Log.Warning(ex, "AckCommand failed for {CommandId}", commandId);
        }
    }

    public record AuditArchiveAck(string ArchiveId, string ArchiveDigest, string Timestamp);

    public async Task<AuditArchiveAck?> UploadAuditArchiveAsync(string archiveJson, string digest, CancellationToken ct)
    {
        var response = await PostSignedAsync("/api/agent/audit-archive",
            new { archive = archiveJson, archiveDigest = digest }, ct);
        if (response == null) return null;
        try
        {
            return JsonSerializer.Deserialize<AuditArchiveAck>(response.Value.GetRawText(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch { return null; }
    }

    public async Task<string?> UploadPomAsync(string pomJson, string digest, CancellationToken ct)
    {
        var response = await PostSignedAsync("/api/agent/pom", new { pom = pomJson, digest }, ct);
        if (response == null) return null;

        try
        {
            if (response.Value.TryGetProperty("pomId", out var id))
                return id.GetString();
        }
        catch { /* malformed response */ }

        return null;
    }

    public void Dispose() => _http.Dispose();
}

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
        if (IsExplicitPhiPath(path, options))
            return;

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

    private static bool IsExplicitPhiPath(string path, AgentOptions options)
    {
        if (string.Equals(path, "/api/agent/patient-details", StringComparison.Ordinal))
            return true;

        return string.Equals(path, "/api/agent/sync", StringComparison.Ordinal) &&
               options.EnableLegacyPhiDeliveryQueueSync;
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

                    // Geographic field (Safe-Harbor identifier) OR a string that only passes the
                    // legacy <=96-char charset without matching a known operational token shape
                    // (the packed-identifier hole, e.g. "DOE-JOHN-1990"). Today both are allowed;
                    // STRICT mode blocks them (positive allow-list). SHADOW mode preserves today's
                    // behavior but logs the would-block so false-positives are measured on a real
                    // pilot before enforcement. Field name only — never the value.
                    case OutboundDecision.GeographicExempt:
                    case OutboundDecision.UnrecognizedToken:
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
    private static readonly Regex KnownOperationalToken = new(
        @"^(?:" +
        @"[0-9a-fA-F]{16,128}" +                                                 // hex hash / digest
        @"|[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}" + // uuid
        @"|\d{1,5}-\d{1,4}-\d{1,2}" +                                            // hyphenated NDC (5-4-2 family)
        @"|\d{6,}" +                                                             // long numeric id (>=6; excludes 5-digit zip)
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
             propertyName is "ndc" or "evidenceid" or "scanwindowid" or "schemaversion" or "schemasignature" or
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
