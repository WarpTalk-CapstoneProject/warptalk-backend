namespace WarpTalk.BillingService.Domain.Enums;

public enum PaymentStatus
{
    Pending,
    Paid,
    Failed,
    Refunded,
    PartiallyRefunded,
    Cancelled
}
