using System.Threading;
using System.Threading.Tasks;
using SuavoAgent.Contracts.Reasoning;
using SuavoAgent.Contracts.Vision;

namespace SuavoAgent.Core.Reasoning;

/// <summary>
/// Observe-only vision-grounded reasoning over a captured screen. Implementations execute
/// nothing — they ground a <see cref="RuleContext"/> on the frame, ask the brain what it would
/// do, and log it. The interface lets the capture worker depend on the behavior, not the concrete
/// brain wiring.
/// </summary>
public interface IVisionShadowReasoner
{
    Task<BrainDecision> ObserveAsync(ScreenFrame frame, string skillId, CancellationToken ct = default);
}
