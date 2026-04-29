namespace WarpTalk.BillingService.Domain.Enums;

public enum TransactionStatus
{
    Pending = 0,
    Processing = 1,
    Success = 2,
    Failed = 3,
    Refunded = 4
}
