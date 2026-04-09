namespace SuavoAgent.Contracts.Models;

public record DeliveryWritebackCommand(
    string TaskId,
    string RxNumber,
    int FillNumber,
    string ExternalSaleId,
    string RecipientFirstName,
    string RecipientLastName,
    int RecipientIdType,
    string RecipientIdValue,
    string RecipientIdState,
    string? SignatureSvg,
    decimal Price,
    decimal Tax,
    int CounselingStatus,
    DateTimeOffset DeliveredAt);
