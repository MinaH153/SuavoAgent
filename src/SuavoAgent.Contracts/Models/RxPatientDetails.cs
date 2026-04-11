namespace SuavoAgent.Contracts.Models;

public record RxPatientDetails(
    string RxNumber,
    string? FirstName,
    string? LastInitial,
    string? Phone,
    string? Address1,
    string? Address2,
    string? City,
    string? State,
    string? Zip);
