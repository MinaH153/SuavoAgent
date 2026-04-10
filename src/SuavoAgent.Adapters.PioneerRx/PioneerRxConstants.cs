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

    // Fallback GUIDs from Care Pharmacy — used when lookup table discovery fails
    public static readonly IReadOnlyDictionary<string, Guid> FallbackStatusGuids =
        new Dictionary<string, Guid>
        {
            [StatusWaitingForPickup] = Guid.Parse("53ce4c47-dff2-46ac-a310-719e792239ef"),
            [StatusWaitingForDelivery] = Guid.Parse("c3adbbcc-76e3-4b06-a0dc-4e8b8ce0a2de"),
            [StatusToBePutInBin] = Guid.Parse("46c30466-375a-4126-a190-8eaf017179c8"),
        };

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
}
