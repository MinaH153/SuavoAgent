using System.Runtime.InteropServices;
using System.Text.Json;
using Serilog;
using SuavoAgent.Contracts.Behavioral;

namespace SuavoAgent.Helper.SystemObservers;

public sealed class StationProfiler
{
    private readonly BehavioralEventBuffer _buffer;
    private readonly string _pharmacySalt;
    private readonly ILogger _logger;

    public StationProfiler(BehavioralEventBuffer buffer, string pharmacySalt, ILogger logger)
    {
        _buffer = buffer;
        _pharmacySalt = pharmacySalt;
        _logger = logger;
    }

    public void CaptureProfile()
    {
        try
        {
            var profile = new
            {
                machineNameHash = UiaPropertyScrubber.HmacHash(Environment.MachineName, _pharmacySalt),
                processorCount = Environment.ProcessorCount,
                ramBucketGb = GetRamBucket(),
                osVersion = Environment.OSVersion.VersionString,
                monitorCount = GetMonitorCount(),
                timestamp = DateTimeOffset.UtcNow.ToString("o")
            };

            var json = JsonSerializer.Serialize(profile);
            _buffer.Enqueue(BehavioralEvent.StationProfileEvent(json));
            _logger.Information("Station profile captured: {Cores} cores, {Ram}GB RAM, {Monitors} monitors",
                profile.processorCount, profile.ramBucketGb, profile.monitorCount);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Station profile capture failed");
        }
    }

    private static int GetRamBucket()
    {
        try
        {
            var totalBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            var gb = (int)(totalBytes / (1024L * 1024 * 1024));
            return gb switch { < 4 => 4, < 8 => 8, < 16 => 16, < 32 => 32, _ => 64 };
        }
        catch { return 0; }
    }

    private static int GetMonitorCount()
    {
        if (!OperatingSystem.IsWindows()) return 1;
        try
        {
            int count = 0;
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
                (IntPtr m, IntPtr h, ref Rect r, IntPtr d) => { count++; return true; }, IntPtr.Zero);
            return count > 0 ? count : 1;
        }
        catch { return 1; }
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref Rect rect, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
}
