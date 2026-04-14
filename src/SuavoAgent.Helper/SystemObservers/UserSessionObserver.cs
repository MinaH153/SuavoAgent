using Serilog;
using SuavoAgent.Contracts.Behavioral;

namespace SuavoAgent.Helper.SystemObservers;

public sealed class UserSessionObserver : IDisposable
{
    private readonly BehavioralEventBuffer _buffer;
    private readonly string _pharmacySalt;
    private readonly ILogger _logger;
    private volatile bool _disposed;

    public int EventCount { get; private set; }

    public UserSessionObserver(BehavioralEventBuffer buffer, string pharmacySalt, ILogger logger)
    {
        _buffer = buffer;
        _pharmacySalt = pharmacySalt;
        _logger = logger;

        if (OperatingSystem.IsWindows())
        {
            Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
            _logger.Information("UserSessionObserver subscribed to session events");
        }
    }

    private void OnSessionSwitch(object? sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        if (_disposed) return;

        var changeType = e.Reason switch
        {
            Microsoft.Win32.SessionSwitchReason.SessionLogon => "logon",
            Microsoft.Win32.SessionSwitchReason.SessionLogoff => "logoff",
            Microsoft.Win32.SessionSwitchReason.SessionLock => "lock",
            Microsoft.Win32.SessionSwitchReason.SessionUnlock => "unlock",
            _ => null
        };
        if (changeType == null) return;

        string? userSidHash = null;
        try
        {
            var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            if (identity.User != null)
                userSidHash = UiaPropertyScrubber.HmacHash(identity.User.Value, _pharmacySalt);
        }
        catch { }

        _buffer.Enqueue(BehavioralEvent.SessionChange(changeType, userSidHash));
        EventCount++;
        _logger.Information("Session event: {Type}", changeType);
    }

    public void Dispose()
    {
        _disposed = true;
        if (OperatingSystem.IsWindows())
            Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
    }
}
