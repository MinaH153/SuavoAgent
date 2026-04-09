namespace SuavoAgent.Adapters.PioneerRx;

public static class PioneerRxConstants
{
    public const string ProcessName = "PioneerPharmacy";
    public const string DefaultWindowTitle = "Point of Sale";

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
        "ClinicalNotes", "PatientNotes"
    };

    public static readonly IReadOnlyList<string> ReadyStatusValues = new[]
    {
        "Ready", "Ready for Delivery", "Filled", "Verified",
        "Waiting for Pickup", "To Be Put in Bin"
    };
}
