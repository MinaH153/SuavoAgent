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
    DetectionSource Source,
    // Patient delivery info — minimum necessary PHI
    string PatientFirstName = "",
    string PatientLastInitial = "",
    string PatientPhone = "",
    string DeliveryAddress1 = "",
    string DeliveryAddress2 = "",
    string DeliveryCity = "",
    string DeliveryState = "",
    string DeliveryZip = "");

public enum DetectionSource { Sql, Uia, Api }
