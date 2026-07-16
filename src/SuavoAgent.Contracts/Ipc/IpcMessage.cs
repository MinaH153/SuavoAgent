using System.Text.Json;
using System.Text.Json.Serialization;

namespace SuavoAgent.Contracts.Ipc;

public record IpcRequest(string Id, string Command, int Version, JsonElement? Data);
public record IpcResponse(string Id, int Status, string Command, JsonElement? Data, IpcError? Error);
public record IpcError(string Code, string Message, bool Retryable, int AttemptCount);

public static class IpcCommands
{
    public const string Ping = "ping";
    public const string AttachPioneerRx = "attach_pioneerrx";
    public const string WritebackDelivery = "writeback_delivery";
    public const string DiscoverScreen = "discover_screen";
    public const string DismissModal = "dismiss_modal";
    public const string CheckUserActivity = "check_user_activity";
    public const string Drain = "drain";
    public const string HelperStatus = "helper_status";
    public const string HelperError = "helper_error";
    public const string GetHealth = "get_health";
    public const string BehavioralEvents = "behavioral_events";
    public const string GetPharmacySalt = "get_pharmacy_salt";
    public const string GetObservationLease = "get_observation_lease";
    public const string SystemEvents = "system_events";
    public const string AutopilotControl = "autopilot_control";
    public const string GetAutopilotControlState = "get_autopilot_control_state";

    // Pricing intelligence — Core→Helper command channel
    public const string PricingLookup = "pricing_lookup";
    public const string PricingObservationContext = "pricing_observation_context";
    public const string PricingJobProgress = "pricing_job_progress";
    public const string PioneerRxTop500Export = "pioneerrx_top500_export";
    public const string PioneerRxTop500ReadArtifact = "pioneerrx_top500_read_artifact";
    public const string PioneerRxPricedWorkbookBegin = "pioneerrx_priced_workbook_begin";
    public const string PioneerRxPricedWorkbookChunk = "pioneerrx_priced_workbook_chunk";
    public const string PioneerRxPricedWorkbookCommit = "pioneerrx_priced_workbook_commit";

    // File discovery — Core→Helper command channel. Helper runs
    // FileLocatorService in the user session; Core can't because it
    // runs as LocalSystem and sees a different filesystem profile.
    public const string FindFile = "find_file";

    // Vision — Core→Helper command channel.
    // Response payload: { storageId: string|null, frame: ScreenFrame }
    public const string CaptureScreen = "capture_screen";
    // First authenticated command on every Core→Helper connection. Helper
    // refuses every machine-vision consumer until this startup-latched identity
    // exactly matches the registry state it loaded.
    public const string VisionStateHandshake = "vision_state_handshake";

    // Intent cursor — Core→Helper command channel.
    // Visual-only overlay in the interactive user session. It must never
    // move the OS cursor, click, type, hook PioneerRx, or carry text labels.
    public const string IntentCursor = "intent_cursor";
}

public sealed record VisionStateHandshake(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("generation")] long Generation,
    [property: JsonPropertyName("configDigest")] string ConfigDigest)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record AutopilotControlRequest(
    int ContractVersion,
    string Action,
    string ReasonCode,
    long? ExpectedControlGeneration)
{
    public const int CurrentContractVersion = 1;
}

public static class IpcStatus
{
    public const int Ok = 200;
    public const int BadRequest = 400;
    public const int Forbidden = 403;
    public const int NotFound = 404;
    public const int Timeout = 408;
    public const int InternalError = 500;
}
