namespace SuavoAgent.Contracts.Ipc;

public record IpcMessage(
    int Version,
    string RequestId,
    string Command,
    string? Payload);

public record IpcResponse(
    string RequestId,
    bool Success,
    string? Result,
    string? Error);

public static class IpcCommands
{
    public const string Ping = "ping";
    public const string ReadGrid = "read_grid";
    public const string WritebackDelivery = "writeback_delivery";
    public const string DiscoverScreen = "discover_screen";
    public const string DismissModal = "dismiss_modal";
    public const string CheckUserActivity = "check_user_activity";
    public const string Drain = "drain";
}
