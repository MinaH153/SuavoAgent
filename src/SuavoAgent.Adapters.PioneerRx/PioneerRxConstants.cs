namespace SuavoAgent.Adapters.PioneerRx;

public static class PioneerRxConstants
{
    public const string ProcessName = "PioneerPharmacy";
    public const string DefaultWindowTitle = "Point of Sale";

    // Status names — used for lookup table discovery (portable across pharmacies)
    public const string StatusWaitingForPickup = "Waiting for Pick up";
    public const string StatusWaitingForDelivery = "Waiting for Delivery";
    public const string StatusToBePutInBin = "To Be Put in Bin";
    public const string StatusOutForDelivery = "Out for Delivery";
    public const string StatusCompleted = "Completed";

    public static readonly IReadOnlyList<string> DeliveryReadyStatusNames = new[]
    {
        StatusWaitingForPickup,
        StatusWaitingForDelivery,
        StatusToBePutInBin
    };

    /// All delivery-related statuses (3 ready + Out for Delivery + Completed).
    /// Used by writeback engine for GUID discovery — all 5 must be discovered for writes.
    public static readonly IReadOnlyList<string> AllDeliveryStatusNames = new[]
    {
        StatusWaitingForPickup,
        StatusWaitingForDelivery,
        StatusToBePutInBin,
        StatusOutForDelivery,
        StatusCompleted
    };

    // No fallback GUIDs — status GUIDs are pharmacy-specific and must be discovered
    // from the live database. Using hardcoded GUIDs would silently produce wrong results.

    /// Pattern-matches status descriptions for delivery-ready states.
    /// More resilient than exact string match — survives vendor text changes.
    public static bool MatchesDeliveryReadyPattern(string description)
    {
        var lower = description.ToLowerInvariant();
        return (lower.Contains("pick") && lower.Contains("up"))
            || (lower.Contains("delivery") && lower.Contains("waiting"))
            || (lower.Contains("bin") && (lower.Contains("put") || lower.Contains("place")));
    }

    /// Pattern-matches any delivery-related status (ready + in-progress + completed).
    public static bool MatchesDeliveryStatusPattern(string description)
    {
        var lower = description.ToLowerInvariant();
        return MatchesDeliveryReadyPattern(description)
            || (lower.Contains("out") && lower.Contains("delivery"))
            || lower.Contains("complet");
    }

    public enum QueryMode
    {
        Detection,
        PatientFetch
    }

    public static readonly IReadOnlySet<string> PhiColumnBlocklist = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase)
    {
        "PatientName", "PatientFirstName", "PatientLastName",
        "PatientDOB", "PatientDateOfBirth", "DateOfBirth",
        "PatientAddress", "PatientAddress1", "PatientAddress2",
        "PatientCity", "PatientState", "PatientZip",
        "PatientPhone", "PatientEmail", "PatientSSN",
        "SSN", "SocialSecurityNumber",
        "DiagnosisCode", "ICD10", "ICD9",
        "PrescriberNotes", "DirectionsForUse",
        "ClinicalNotes", "PatientNotes",
        "PatientID", "PersonID"
    };

    private static readonly string[] PhiColumnPatterns =
    {
        "patient", "ssn", "dob", "birth", "phone", "address",
        "email", "person", "contact", "emergency", "guardian",
        "social", "security", "mobile", "fax"
    };

    public static bool IsPhiColumn(string columnName)
    {
        if (PhiColumnBlocklist.Contains(columnName))
            return true;

        var lower = columnName.ToLowerInvariant();
        foreach (var pattern in PhiColumnPatterns)
        {
            if (lower.Contains(pattern))
                return true;
        }
        return false;
    }
}
