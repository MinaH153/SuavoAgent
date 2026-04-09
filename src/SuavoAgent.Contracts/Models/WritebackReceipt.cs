namespace SuavoAgent.Contracts.Models;

public record WritebackReceipt(
    bool Success,
    string? TransactionId,
    string? Error,
    WritebackMethod Method,
    bool Verified,
    DateTimeOffset CompletedAt);

public enum WritebackMethod { Api, Uia, Manual }
