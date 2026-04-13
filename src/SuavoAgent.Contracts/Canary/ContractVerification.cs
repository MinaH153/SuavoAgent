namespace SuavoAgent.Contracts.Canary;

public record ContractVerification(
    bool IsValid,
    CanarySeverity Severity,
    IReadOnlyList<string> DriftedComponents,
    string? BaselineHash,
    string? ObservedHash,
    string? Details)
{
    public static ContractVerification Clean { get; } = new(true, CanarySeverity.None,
        Array.Empty<string>(), null, null, null);
}
