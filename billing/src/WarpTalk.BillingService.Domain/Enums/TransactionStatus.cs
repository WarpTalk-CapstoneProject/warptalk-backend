namespace WarpTalk.BillingService.Domain.Enums;

public enum TransactionStatus
{
    Pending = 0,
    Succeeded = 1,
    Failed = 2,
    Refunded = 3,
    Cancelled = 4
}
