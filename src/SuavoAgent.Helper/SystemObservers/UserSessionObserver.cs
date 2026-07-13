using Serilog;
using SuavoAgent.Contracts.Behavioral;

namespace SuavoAgent.Helper.SystemObservers;

public sealed class UserSessionObserver : IDisposable
{
    private readonly BehavioralEventBuffer _buffer;
    private readonly string _pharmacySalt;
    private readonly ILogger _logger;
    private volatile bool _disposed;
    private bool _subscribed;

    public int EventCount { get; private set; }
    public bool IsAvailable { get; private set; }

    public UserSessionObserver(BehavioralEventBuffer buffer, string pharmacySalt, ILogger logger)
    {
        _buffer = buffer;
        _pharmacySalt = pharmacySalt;
        _logger = logger;

        if (OperatingSystem.IsWindows())
        {
            try
            {
                Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
                _subscribed = true;
                IsAvailable = true;
                _buffer.Enqueue(BehavioralEvent.ObserverStatus("user_session", "ready"));
                _logger.Information("UserSessionObserver subscribed to session events");
            }
            catch (Exception ex)
            {
                _logger.Warning(
                    "UserSessionObserver subscription failed ({ExceptionType})",
                    ex.GetType().FullName);
                _buffer.Enqueue(BehavioralEvent.ObserverStatus("user_session", "subscription_failed"));
            }
        }
        else
        {
            _buffer.Enqueue(BehavioralEvent.ObserverStatus("user_session", "unsupported_platform"));
        }
    }

    private void OnSessionSwitch(object? sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        if (_disposed) return;

        var changeType = MapReason(e.Reason);
        if (changeType == null) return;

        string? userSidHash = null;
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            if (identity.User != null)
                userSidHash = UiaPropertyScrubber.HmacHash(identity.User.Value, _pharmacySalt);
        }
        catch { }

        _buffer.Enqueue(BehavioralEvent.SessionChange(changeType, userSidHash));
        EventCount++;
        _logger.Information("Session event: {Type}", changeType);
    }

    internal static string? MapReason(Microsoft.Win32.SessionSwitchReason reason) => reason switch
    {
        Microsoft.Win32.SessionSwitchReason.SessionLogon => "logon",
        Microsoft.Win32.SessionSwitchReason.SessionLogoff => "logoff",
        Microsoft.Win32.SessionSwitchReason.SessionLock => "lock",
        Microsoft.Win32.SessionSwitchReason.SessionUnlock => "unlock",
        Microsoft.Win32.SessionSwitchReason.RemoteConnect => "rdp_connect",
        Microsoft.Win32.SessionSwitchReason.RemoteDisconnect => "rdp_disconnect",
        Microsoft.Win32.SessionSwitchReason.ConsoleConnect => "console_connect",
        Microsoft.Win32.SessionSwitchReason.ConsoleDisconnect => "console_disconnect",
        _ => null,
    };

    public void Dispose()
    {
        _disposed = true;
        if (_subscribed && OperatingSystem.IsWindows())
            Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
        _subscribed = false;
    }
}
