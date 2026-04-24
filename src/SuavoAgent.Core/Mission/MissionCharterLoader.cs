using System.Text.Json;
using System.Text.Json.Serialization;

namespace SuavoAgent.Core.Mission;

/// <summary>
/// Loads a <see cref="MissionCharter"/> for a given pharmacy.
///
/// Scaffolding: reads from a JSON file when <see cref="CharterFilePathResolver"/>
/// points at an existing file, otherwise returns the default charter built by
/// <see cref="BuildDefaultCharter"/>. Production will front a cloud-signed
/// charter store; the shape of the record is the API boundary and will not
/// change under the loader.
/// </summary>
public sealed class MissionCharterLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Given a pharmacy id, return the charter-JSON path to probe. Default
    /// resolver returns <c>null</c> so the loader falls back to the default
    /// charter. Tests and installers swap this for file-backed loading.
    /// </summary>
    public Func<string, string?> CharterFilePathResolver { get; set; } = _ => null;

    /// <summary>
    /// Load a charter for the given pharmacy. Validates before returning.
    /// Throws <see cref="MissionCharterInvalidException"/> if the loaded
    /// charter fails a structural invariant.
    /// </summary>
    public async Task<MissionCharter> LoadAsync(string pharmacyId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(pharmacyId))
        {
            throw new ArgumentException("pharmacyId must be non-empty", nameof(pharmacyId));
        }

        var path = CharterFilePathResolver(pharmacyId);
        MissionCharter charter;
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            await using var stream = File.OpenRead(path);
            var loaded = await JsonSerializer.DeserializeAsync<MissionCharter>(stream, JsonOptions, ct)
                .ConfigureAwait(false);
            if (loaded is null)
            {
                throw new MissionCharterInvalidException(
                    "CHARTER_DESERIALIZE_NULL",
                    $"Charter file deserialised to null: {path}");
            }
            charter = loaded;
        }
        else
        {
            charter = BuildDefaultCharter(pharmacyId);
        }

        ValidateCharter(charter);
        return charter;
    }

    /// <summary>
    /// Structural validation. Throws <see cref="MissionCharterInvalidException"/>
    /// with a specific rule id on the first failed check.
    /// </summary>
    public static void ValidateCharter(MissionCharter charter)
    {
        if (charter is null)
        {
            throw new ArgumentNullException(nameof(charter));
        }

        if (charter.CharterId == Guid.Empty)
        {
            throw new MissionCharterInvalidException(
                "CHARTER_ID_EMPTY",
                "CharterId must be a non-empty GUID");
        }

        if (string.IsNullOrWhiteSpace(charter.PharmacyId))
        {
            throw new MissionCharterInvalidException(
                "PHARMACY_ID_EMPTY",
                "PharmacyId must be non-empty");
        }

        if (charter.Version <= 0)
        {
            throw new MissionCharterInvalidException(
                "VERSION_NON_POSITIVE",
                $"Version must be > 0, got {charter.Version}");
        }

        if (charter.Objectives is null || charter.Objectives.Count == 0)
        {
            throw new MissionCharterInvalidException(
                "OBJECTIVES_EMPTY",
                "Objectives must contain at least one entry");
        }

        var objectiveIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var obj in charter.Objectives)
        {
            if (obj is null || string.IsNullOrWhiteSpace(obj.Id))
            {
                throw new MissionCharterInvalidException(
                    "OBJECTIVE_ID_EMPTY",
                    "Every objective must have a non-empty Id");
            }

            if (!objectiveIds.Add(obj.Id))
            {
                throw new MissionCharterInvalidException(
                    "OBJECTIVE_ID_DUPLICATE",
                    $"Duplicate objective id: {obj.Id}");
            }
        }

        if (charter.PriorityOrdering is null ||
            charter.PriorityOrdering.OrderedObjectiveIds is null)
        {
            throw new MissionCharterInvalidException(
                "PRIORITY_ORDERING_NULL",
                "PriorityOrdering and its OrderedObjectiveIds must be non-null");
        }

        var orderedIds = charter.PriorityOrdering.OrderedObjectiveIds;
        if (orderedIds.Count != objectiveIds.Count)
        {
            throw new MissionCharterInvalidException(
                "PRIORITY_ORDERING_CARDINALITY",
                $"PriorityOrdering has {orderedIds.Count} ids but charter has {objectiveIds.Count} objectives");
        }

        var orderedSet = new HashSet<string>(orderedIds, StringComparer.Ordinal);
        if (orderedSet.Count != orderedIds.Count)
        {
            throw new MissionCharterInvalidException(
                "PRIORITY_ORDERING_DUPLICATE",
                "PriorityOrdering contains duplicate ids");
        }

        if (!orderedSet.SetEquals(objectiveIds))
        {
            throw new MissionCharterInvalidException(
                "PRIORITY_ORDERING_PERMUTATION",
                "PriorityOrdering must be a permutation of objective ids");
        }

        if (charter.Constraints is not null)
        {
            var constraintIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in charter.Constraints)
            {
                if (c is null || string.IsNullOrWhiteSpace(c.Id))
                {
                    throw new MissionCharterInvalidException(
                        "CONSTRAINT_ID_EMPTY",
                        "Every constraint must have a non-empty Id");
                }
                if (!constraintIds.Add(c.Id))
                {
                    throw new MissionCharterInvalidException(
                        "CONSTRAINT_ID_DUPLICATE",
                        $"Duplicate constraint id: {c.Id}");
                }
            }
        }

        if (charter.Tolerance is null)
        {
            throw new MissionCharterInvalidException(
                "TOLERANCE_NULL",
                "Tolerance thresholds must be non-null");
        }

        if (charter.Tolerance.MaxDowntimeSecondsPerShift < 0 ||
            charter.Tolerance.MaxRetriesBeforeEscalation < 0 ||
            charter.Tolerance.MinCacheHitRateForAutonomy < 0)
        {
            throw new MissionCharterInvalidException(
                "TOLERANCE_NEGATIVE",
                "Tolerance thresholds must be non-negative");
        }
    }

    /// <summary>
    /// Minimal conservative charter used when no on-disk charter exists. Keeps
    /// the substrate runnable without a provisioned pharmacy and documents the
    /// baseline values a real charter must override.
    /// </summary>
    internal static MissionCharter BuildDefaultCharter(string pharmacyId)
    {
        var objectives = new List<MissionObjective>
        {
            new("keep-agent-alive", "Agent heartbeat uninterrupted", 100),
            new("detect-ready-rx", "Detect ready-for-delivery prescriptions within SLA", 80),
            new("protect-phi", "Never exfiltrate PHI outside approved boundaries", 100),
        };

        var constraints = new List<MissionConstraint>
        {
            new("no-unsigned-verbs",
                "policy",
                "verb.signature != null",
                "Action grammar v1 — every verb must be signed."),
            new("respect-baa-scope",
                "policy",
                "verb.BaaScope != None || invariant.PhiRecordsExposed == 0",
                "HIPAA BAA boundary."),
        };

        var priority = new MissionPriorityOrdering(
            new List<string> { "protect-phi", "keep-agent-alive", "detect-ready-rx" });

        var tolerance = new MissionToleranceThresholds(
            MaxDowntimeSecondsPerShift: 120,
            MaxRetriesBeforeEscalation: 3,
            MinCacheHitRateForAutonomy: 0.90);

        return new MissionCharter(
            CharterId: Guid.NewGuid(),
            PharmacyId: pharmacyId,
            Version: 1,
            EffectiveFrom: DateTimeOffset.UtcNow,
            Objectives: objectives,
            Constraints: constraints,
            PriorityOrdering: priority,
            Tolerance: tolerance,
            SignedByOperator: "scaffold-default",
            SignedAt: DateTimeOffset.UtcNow);
    }
}
