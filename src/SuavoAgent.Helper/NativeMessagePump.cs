using System.Runtime.InteropServices;

namespace SuavoAgent.Helper;

/// <summary>
/// P/Invoke declarations for Win32 message pump.
/// Required for WH_KEYBOARD_LL — the hook callback only fires
/// when the installing thread pumps messages.
/// </summary>
internal static class NativeMessagePump
{
    private const uint PM_REMOVE = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    [DllImport("user32.dll")]
    public static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessage(ref MSG lpMsg);

    /// <summary>
    /// Installs the keyboard hook on this thread, then pumps messages until cancelled.
    /// Must be called on a dedicated STA thread.
    /// </summary>
    public static void RunHookPump(Behavioral.KeyboardCategoryHook hook, CancellationToken ct)
    {
        hook.Install();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (PeekMessage(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE))
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
                else
                {
                    Thread.Sleep(10); // Don't spin CPU
                }
            }
        }
        finally
        {
            hook.Uninstall();
        }
    }
}
