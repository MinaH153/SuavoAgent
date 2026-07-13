using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Core.Vision;

namespace SuavoAgent.Helper.Vision;

/// <summary>
/// Process-local, immutable runtime truth for vision/OCR. Configuration is
/// fixed at Helper startup; only the readiness verdict is replaced as the
/// exact OCR runtime loads, fails, or recovers. Snapshot content is restricted
/// to the closed PHI-free contract in <see cref="VisionRuntimeReadiness"/>.
/// </summary>
public sealed class VisionRuntimeStatusTracker
{
    private readonly bool _visionEnabled;
    private readonly bool _ocrConfigured;
    private readonly long _generation;
    private readonly Func<DateTimeOffset> _clock;
    private VisionRuntimeReadiness _current;

    public VisionRuntimeStatusTracker(
        VisionConfigurationLoadResult configuration,
        Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!configuration.IsValid)
            throw new ArgumentException("Vision configuration must be valid.", nameof(configuration));

        _visionEnabled = configuration.EffectiveOptions.Enabled;
        _ocrConfigured = configuration.EffectiveOptions.Tesseract.Enabled;
        _generation = configuration.EffectiveGeneration;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _current = Create(
            ready: false,
            ocrReady: false,
            _visionEnabled ? VisionRuntimeCodes.VisionStarting : VisionRuntimeCodes.VisionDisabled);
    }

    public VisionRuntimeReadiness Snapshot() => Volatile.Read(ref _current);

    public void RecordPlatformUnsupported() =>
        Replace(false, false, VisionRuntimeCodes.VisionPlatformUnsupported);

    public void RecordReady(bool ocrReady) => Replace(
        ready: true,
        ocrReady,
        ocrReady ? VisionRuntimeCodes.VisionReadyOcr : VisionRuntimeCodes.VisionReadyUiaOnly);

    public void RecordFailure(string code)
    {
        if (!VisionRuntimeCodes.All.Contains(code) || code is
            VisionRuntimeCodes.VisionDisabled or
            VisionRuntimeCodes.VisionStarting or
            VisionRuntimeCodes.VisionReadyUiaOnly or
            VisionRuntimeCodes.VisionReadyOcr)
            throw new ArgumentOutOfRangeException(nameof(code));
        Replace(false, false, code);
    }

    private void Replace(bool ready, bool ocrReady, string code)
    {
        var next = Create(ready, ocrReady, code);
        if (!next.IsValid())
            throw new InvalidOperationException("Vision runtime status invariant failed.");
        Volatile.Write(ref _current, next);
    }

    private VisionRuntimeReadiness Create(bool ready, bool ocrReady, string code) => new(
        VisionRuntimeReadiness.CurrentContractVersion,
        _visionEnabled,
        _ocrConfigured,
        ready,
        ocrReady,
        code,
        _generation,
        _clock());
}

/// <summary>
/// Startup failure carrying only a closed, PHI-free runtime code. Never pass
/// filesystem paths, hashes, OCR content, or nested exception messages.
/// </summary>
internal sealed class VisionRuntimeUnavailableException : InvalidOperationException
{
    internal VisionRuntimeUnavailableException(string code) : base(code)
    {
        if (!VisionRuntimeCodes.All.Contains(code))
            throw new ArgumentOutOfRangeException(nameof(code));
        Code = code;
    }

    internal string Code { get; }
}
