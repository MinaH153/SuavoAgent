using SuavoAgent.Contracts.Health;
using SuavoAgent.Contracts.Models;

namespace SuavoAgent.Contracts.Adapters;

public interface ILocalPmsAdapter
{
    string PmsName { get; }
    Task<CapabilityManifest> DiscoverCapabilitiesAsync(CancellationToken ct);
    Task<IReadOnlyList<RxReadyForDelivery>> PullReadyAsync(string? cursor, CancellationToken ct);
    Task<WritebackReceipt> SubmitWritebackAsync(DeliveryWritebackCommand cmd, CancellationToken ct);
    Task<bool> VerifyWritebackAsync(WritebackReceipt receipt, CancellationToken ct);
    Task<AdapterHealthReport> CheckHealthAsync(CancellationToken ct);
}
