using System.Text.Json;

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
}

public static class IpcStatus
{
    public const int Ok = 200;
    public const int BadRequest = 400;
    public const int NotFound = 404;
    public const int Timeout = 408;
    public const int InternalError = 500;
}
