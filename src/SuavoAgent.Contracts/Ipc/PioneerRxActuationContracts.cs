using System.Text.Json.Serialization;

namespace SuavoAgent.Contracts.Ipc;

/// <summary>
/// Phase 5.3 — PioneerRx-specific actuation IPC contracts. SEPARATE from
/// the Phase 5.2 sandbox contracts so the process allowlist enforced
/// Helper-side cannot be confused: <c>actuation.click_by_label</c>
/// targets {Notepad, Calc} only; <c>actuation.pioneerrx_click</c> targets
/// PioneerPharmacy.exe only.
///
/// Every command here carries a BAA scope tag the cloud must verify
/// against pharmacy_profiles.baa_amendments before dispatch — the agent
/// also re-checks the scope locally as defense in depth.
/// </summary>
public static class PioneerRxActuationIpcCommands
{
    public const string Click = "actuation.pioneerrx_click";
    public const string TypeText = "actuation.pioneerrx_type_text";
    public const string Query = "actuation.pioneerrx_query";
    public const string WritebackRxDelivery = "actuation.pioneerrx_writeback_rx_delivery";
}

public sealed record PioneerRxClickRequest(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("matchMode")] string MatchMode,
    [property: JsonPropertyName("timeoutMs")] int TimeoutMs,
    [property: JsonPropertyName("baaScopeTag")] string BaaScopeTag
);

public sealed record PioneerRxTypeTextRequest(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("clearFirst")] bool ClearFirst,
    [property: JsonPropertyName("perKeyDelayMs")] int PerKeyDelayMs,
    [property: JsonPropertyName("baaScopeTag")] string BaaScopeTag
);

public sealed record PioneerRxQueryRequest(
    [property: JsonPropertyName("queryKind")] string QueryKind,
    [property: JsonPropertyName("queryArg")] string QueryArg,
    [property: JsonPropertyName("baaScopeTag")] string BaaScopeTag
);

public sealed record PioneerRxWritebackRxDeliveryRequest(
    [property: JsonPropertyName("rxNumber")] string RxNumber,
    [property: JsonPropertyName("deliveryStatus")] string DeliveryStatus,
    [property: JsonPropertyName("deliveredAt")] DateTimeOffset DeliveredAt,
    [property: JsonPropertyName("baaScopeTag")] string BaaScopeTag,
    [property: JsonPropertyName("baaAmendmentId")] string BaaAmendmentId
);

public static class PioneerRxAllowlistedProcess
{
    public const string Process = "PioneerPharmacy.exe";
}
