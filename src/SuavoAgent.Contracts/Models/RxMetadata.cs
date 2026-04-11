namespace SuavoAgent.Contracts.Models;

public record RxMetadata(
    string RxNumber,
    string? DrugName,
    string? Ndc,
    DateTime? DateFilled,
    int? Quantity,
    Guid StatusGuid);
