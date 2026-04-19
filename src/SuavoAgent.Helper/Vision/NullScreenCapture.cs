using SuavoAgent.Contracts.Vision;

namespace SuavoAgent.Helper.Vision;

/// <summary>
/// No-op capture. Used when VisionOptions.Enabled=false or the platform
/// doesn't support capture (tests on macOS, for example). Always returns null.
/// </summary>
public sealed class NullScreenCapture : IScreenCapture
{
    public bool IsAvailable => false;
    public Task<ScreenBytes?> CapturePrimaryAsync(CancellationToken ct) =>
        Task.FromResult<ScreenBytes?>(null);
}
