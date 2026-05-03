// =======================================================
// Domain/Enums/TransactionStatus.cs
// =======================================================

namespace WarpTalk.BillingService.Domain.Enums;

public enum TransactionStatus
{
    Pending = 0,

    Processing = 1,

    RequiresAction = 2,

    Success = 3,

    Failed = 4,

    Cancelled = 5,

    Expired = 6,

    Refunded = 7,

    Chargeback = 8
}