namespace SuavoAgent.Contracts.Security;

/// <summary>
/// Single source of truth for the Windows identity boundary of SuavoAgent.Core.
/// The service continues to run under the least-privileged LocalService account,
/// but Windows adds this unique service SID to its token when the service is
/// configured with SERVICE_SID_TYPE_UNRESTRICTED.
/// </summary>
public static class CoreServiceIdentity
{
    public const string ServiceName = "SuavoAgent.Core";
    public const string ExecutableName = "SuavoAgent.Core.exe";
    public const string AccountName = @"NT AUTHORITY\LocalService";

    // Deterministic Windows service SID for the uppercase UTF-16 service name.
    // Keep this literal stable: changing it requires a coordinated installer,
    // runtime, IPC, and protected-state migration.
    public const string ServiceSid =
        "S-1-5-80-3161787503-2860973704-3751597344-303720228-1013404410";

}
