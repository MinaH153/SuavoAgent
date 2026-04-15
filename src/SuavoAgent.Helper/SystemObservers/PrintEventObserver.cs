using Serilog;
using SuavoAgent.Contracts.Behavioral;

namespace SuavoAgent.Helper.SystemObservers;

public sealed class PrintEventObserver : IDisposable
{
    private readonly BehavioralEventBuffer _buffer;
    private readonly string _pharmacySalt;
    private readonly ILogger _logger;
    private volatile bool _disposed;
    private readonly HashSet<int> _seenJobHashes = new();

    public int PrintEventCount { get; private set; }

    public PrintEventObserver(BehavioralEventBuffer buffer, string pharmacySalt, ILogger logger)
    {
        _buffer = buffer;
        _pharmacySalt = pharmacySalt;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            _logger.Debug("PrintEventObserver: not on Windows, skipping");
            return;
        }

        _logger.Information("PrintEventObserver started");
        while (!ct.IsCancellationRequested && !_disposed)
        {
            try { PollPrintJobs(); }
            catch (Exception ex) { _logger.Debug(ex, "PrintEventObserver poll error"); }
            await Task.Delay(10000, ct);
        }
    }

    private void PollPrintJobs()
    {
        try
        {
            var spoolerDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "spool", "PRINTERS");

            if (!Directory.Exists(spoolerDir)) return;

            var spoolFiles = Directory.GetFiles(spoolerDir, "*.SPL");
            var currentHashes = new HashSet<int>();

            foreach (var file in spoolFiles)
            {
                var jobHash = file.GetHashCode();
                currentHashes.Add(jobHash);

                if (_seenJobHashes.Contains(jobHash)) continue; // already processed
                _seenJobHashes.Add(jobHash);

                var evt = BehavioralEvent.Interaction(
                    subtype: "print_job",
                    treeHash: null,
                    elementId: "print",
                    controlType: "printer",
                    className: null,
                    nameHash: UiaPropertyScrubber.HmacHash(Path.GetFileName(file), _pharmacySalt)
                );
                _buffer.Enqueue(evt);
                PrintEventCount++;
            }

            // Remove hashes for files that no longer exist (completed jobs)
            _seenJobHashes.IntersectWith(currentHashes);
        }
        catch { }
    }

    public void Dispose() => _disposed = true;
}
