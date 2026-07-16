namespace SuavoAgent.Contracts.Security;

/// <summary>
/// Executable boundary for the exact policy whose digest is signed into every
/// activation lease. The pharmacy field cohort is deliberately limited to the
/// locally-approved PioneerRx window; broader workstation observation requires
/// a new disclosed policy, digest, and control-plane authorization.
/// </summary>
public static class ObservationActivationPolicy
{
    public static bool AllowsApprovedPioneerRxObservation => true;
    public static bool AllowsBrowserObservation => false;
    public static bool AllowsMultiApplicationObservation => false;
    public static bool AllowsMultiApplicationActuation => false;
    public static bool AllowsFileSystemDiscovery => false;
    public static bool AllowsPrintObservation => false;
    public static bool AllowsSpreadsheetObservation => false;
}
