using System.Security.Cryptography;
using System.Text;
using Serilog;
using SuavoAgent.Contracts.Behavioral;

namespace SuavoAgent.Helper.SystemObservers;

public sealed class MultiAppUiaObserver
{
    private readonly BehavioralEventBuffer _buffer;
    private readonly string _pharmacySalt;
    private readonly ILogger _logger;
    private string? _lastTreeHash;
    private DateTimeOffset _lastSnapshot = DateTimeOffset.MinValue;
    public int SnapshotCount { get; private set; }

    public MultiAppUiaObserver(BehavioralEventBuffer buffer, string pharmacySalt, ILogger logger)
    { _buffer = buffer; _pharmacySalt = pharmacySalt; _logger = logger; }

    public void OnAppFocused(string processName, string? windowTitle)
    {
        if (DateTimeOffset.UtcNow - _lastSnapshot < TimeSpan.FromSeconds(30)) return;
        var titleHash = windowTitle != null ? UiaPropertyScrubber.HmacHash(windowTitle, _pharmacySalt) : "no-title";
        var treeHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{processName}:{titleHash}")))[..16].ToLowerInvariant();
        if (treeHash == _lastTreeHash) return;
        _lastTreeHash = treeHash;
        _lastSnapshot = DateTimeOffset.UtcNow;
        _buffer.Enqueue(new BehavioralEvent { Type = BehavioralEventType.TreeSnapshot, Subtype = processName, TreeHash = treeHash, OccurrenceCount = 1, Timestamp = DateTimeOffset.UtcNow });
        SnapshotCount++;
    }
}
