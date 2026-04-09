namespace SuavoAgent.Contracts.Models;

public record RxReadyForDelivery(
    string RxNumber,
    int FillNumber,
    string DrugName,
    string Ndc,
    decimal Quantity,
    int DaysSupply,
    string StatusText,
    bool IsControlled,
    int? DrugSchedule,
    bool PatientIdRequired,
    bool CounselingRequired,
    DateTimeOffset DetectedAt,
    DetectionSource Source);

public enum DetectionSource { Sql, Uia, Api }
