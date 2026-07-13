using System.Runtime.Versioning;
using Serilog;

namespace SuavoAgent.Helper.Actuation;

/// <summary>
/// Owns the shared message-pump thread for <see cref="UserInputObserver"/>
/// and <see cref="HotkeyKillSwitch"/>. Both APIs require a thread that
/// pumps Win32 messages — we do it once here rather than starting two.
///
/// Lifetime: one per Helper process, started in Program.cs after the gate
/// + driver are constructed, stopped on shutdown.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ActuationRuntime : IDisposable
{
    private readonly ActuationGate _gate;
    private readonly UserInputObserver _userInput;
    private readonly HotkeyKillSwitch _hotkey;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private Thread? _hookThread;
    private Thread? _hotkeyThread;
    private bool _disposed;

    public ActuationRuntime(ActuationGate gate, UserInputObserver userInput, HotkeyKillSwitch hotkey, ILogger logger)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _userInput = userInput ?? throw new ArgumentNullException(nameof(userInput));
        _hotkey = hotkey ?? throw new ArgumentNullException(nameof(hotkey));
        _logger = (logger ?? throw new ArgumentNullException(nameof(logger))).ForContext<ActuationRuntime>();
    }

    public void Start()
    {
        _hookThread = new Thread(() => HookPumpLoop(_cts.Token))
        {
            IsBackground = true,
            Name = "ActuationHookPump",
        };
        _hookThread.SetApartmentState(ApartmentState.STA);
        _hookThread.Start();

        _hotkeyThread = new Thread(() => _hotkey.RunPump(_cts.Token))
        {
            IsBackground = true,
            Name = "ActuationHotkeyPump",
        };
        _hotkeyThread.SetApartmentState(ApartmentState.STA);
        _hotkeyThread.Start();

        _logger.Information("ActuationRuntime started (hook pump + hotkey pump)");
    }

    private void HookPumpLoop(CancellationToken ct)
    {
        try
        {
            _userInput.Install();
            // Pump messages on this thread for the hook callbacks to fire.
            // Fail-closed: if the hook can't install, we don't enable the gate.
            while (!ct.IsCancellationRequested)
            {
                if (NativeMessagePump.PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1))
                {
                    NativeMessagePump.TranslateMessage(ref msg);
                    NativeMessagePump.DispatchMessage(ref msg);
                }
                else
                {
                    Thread.Sleep(20);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(
                "ActuationRuntime hook pump failed ({ErrorType}) — tripping kill switch (fail-closed)",
                ex.GetType().Name);
            _gate.TripKillSwitch("hook_pump_failure");
        }
        finally
        {
            _userInput.Uninstall();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        try { _hookThread?.Join(TimeSpan.FromSeconds(2)); } catch { }
        try { _hotkeyThread?.Join(TimeSpan.FromSeconds(2)); } catch { }
        _userInput.Dispose();
        _hotkey.Dispose();
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }
}
