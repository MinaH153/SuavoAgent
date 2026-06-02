using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SuavoAgent.Contracts.Models;

namespace SuavoAgent.Core.Workers;

internal static class PioneerRxShadowFixtureExporter
{
    private static readonly JsonSerializerOptions FixtureJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = true,
    };

    public static string Export(
        IReadOnlyList<RxMetadata> sourceRxs,
        PioneerRxShadowFixtureExportOptions options)
    {
        ArgumentNullException.ThrowIfNull(sourceRxs);
        ArgumentNullException.ThrowIfNull(options);

        var rows = sourceRxs
            .Take(Math.Max(0, options.MaxRows))
            .Select((rx, index) => ToFixtureRx(rx, index, options.SerializedAtUtc))
            .ToArray();

        var patients = options.IncludeSyntheticPatientDetails
            ? rows.Select(row => new FixturePatient(
                row.RxNumber,
                FirstName: $"ShadowPatient{row.Ordinal:000}",
                LastInitial: "S",
                Phone: $"619555{row.Ordinal:0000}",
                Address1: $"{700 + row.Ordinal} Shadow Test Ave",
                Address2: null,
                City: "San Diego",
                State: "CA",
                Zip: "92101"))
                .ToArray()
            : Array.Empty<FixturePatient>();

        // Drug name appears cleartext in the legacy rxDeliveryQueue but is
        // hashed (nameHash) in the canonical rxOrderCandidates. It is
        // forbidden in candidate, required (as operational metadata) in
        // queue.
        var drugNames = rows
            .Select(row => row.DrugName)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToList();

        // Directly-identifying patient PHI (name, phone, street address)
        // is forbidden EVERYWHERE on the agent→cloud sync wire as of
        // 2026-05-12 (Codex CRITICAL #15 / Track 3 invariant). The legacy
        // queue no longer carries these fields; patient delivery details
        // flow exclusively through SendPatientDetailsAsync.
        var patientPhi = patients
            .SelectMany(patient => new[]
            {
                patient.FirstName,
                patient.Phone,
                patient.Address1,
            })
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToList();

        var forbiddenInCandidate = new List<string>(drugNames);
        forbiddenInCandidate.AddRange(patientPhi);

        var forbiddenEverywhere = new List<string>(rows.Select(row => row.RxNumber));
        forbiddenEverywhere.AddRange(patientPhi);

        var fixture = new FixtureRoot(
            SerializedAtUtc: options.SerializedAtUtc,
            HmacSalt: options.HmacSalt,
            HashKeyVersion: options.HashKeyVersion,
            PharmacyId: options.PharmacyId,
            AgentInstallId: options.AgentInstallId,
            PmsVersion: options.PmsVersion,
            Rxs: rows,
            Patients: patients,
            ForbiddenInCandidate: forbiddenInCandidate,
            ForbiddenEverywhere: forbiddenEverywhere,
            MinimumNecessaryInQueue: drugNames);

        var json = JsonSerializer.Serialize(fixture, FixtureJsonOptions);
        EnsureSourceTokensAbsent(json, sourceRxs);
        return json;
    }

    public static string ComputeSha256Prefix(string json, int prefixLength = 16)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        var hex = Convert.ToHexString(hash).ToLowerInvariant();
        return hex[..Math.Clamp(prefixLength, 1, hex.Length)];
    }

    private static FixtureRx ToFixtureRx(
        RxMetadata source,
        int index,
        DateTimeOffset serializedAtUtc)
    {
        var ordinal = index + 1;
        return new FixtureRx(
            Ordinal: ordinal,
            RxNumber: $"SHADOW-RX-{ordinal:0000}",
            DrugName: string.IsNullOrWhiteSpace(source.DrugName) ? null : $"ShadowMed {ordinal:000}",
            Ndc: string.IsNullOrWhiteSpace(source.Ndc) ? null : $"00000-0000-{ordinal % 100:00}",
            DateFilled: source.DateFilled.HasValue
                ? serializedAtUtc.UtcDateTime.Date.AddMinutes(ordinal)
                : null,
            Quantity: source.Quantity,
            StatusGuid: source.StatusGuid,
            DetectedAt: serializedAtUtc.AddSeconds(ordinal),
            FillNumber: source.FillNumber,
            DaysSupply: source.DaysSupply,
            DrugSchedule: source.DrugSchedule,
            Priority: source.Priority,
            TemperatureRequirement: source.TemperatureRequirement);
    }

    private static void EnsureSourceTokensAbsent(string json, IReadOnlyList<RxMetadata> sourceRxs)
    {
        foreach (var token in sourceRxs.SelectMany(SourceTokens))
        {
            if (json.Contains(JsonSerializer.Serialize(token), StringComparison.Ordinal))
            {
                throw new InvalidOperationException("PioneerRx shadow fixture export failed PHI scrub check.");
            }
        }
    }

    private static IEnumerable<string> SourceTokens(RxMetadata rx)
    {
        if (!string.IsNullOrWhiteSpace(rx.RxNumber))
            yield return rx.RxNumber;
        if (!string.IsNullOrWhiteSpace(rx.DrugName))
            yield return rx.DrugName;
        if (!string.IsNullOrWhiteSpace(rx.Ndc))
            yield return rx.Ndc;
        if (rx.DateFilled.HasValue)
            yield return rx.DateFilled.Value.ToString("O");
    }

    private sealed record FixtureRoot(
        DateTimeOffset SerializedAtUtc,
        string HmacSalt,
        string HashKeyVersion,
        string PharmacyId,
        string AgentInstallId,
        string PmsVersion,
        IReadOnlyList<FixtureRx> Rxs,
        IReadOnlyList<FixturePatient> Patients,
        IReadOnlyList<string> ForbiddenInCandidate,
        IReadOnlyList<string> ForbiddenEverywhere,
        IReadOnlyList<string> MinimumNecessaryInQueue);

    private sealed record FixtureRx(
        [property: JsonIgnore] int Ordinal,
        string RxNumber,
        string? DrugName,
        string? Ndc,
        DateTime? DateFilled,
        decimal Quantity,
        Guid StatusGuid,
        DateTimeOffset DetectedAt,
        int FillNumber,
        int DaysSupply,
        int? DrugSchedule,
        string Priority,
        string TemperatureRequirement);

    private sealed record FixturePatient(
        string RxNumber,
        string? FirstName,
        string? LastInitial,
        string? Phone,
        string? Address1,
        string? Address2,
        string? City,
        string? State,
        string? Zip);
}

internal sealed record PioneerRxShadowFixtureExportOptions(
    DateTimeOffset SerializedAtUtc,
    string PharmacyId,
    string AgentInstallId,
    string PmsVersion,
    bool IncludeSyntheticPatientDetails = false,
    int MaxRows = 5,
    string HmacSalt = "shadow-fixture-hmac-salt-v1",
    string HashKeyVersion = "shadow-key-v1");
