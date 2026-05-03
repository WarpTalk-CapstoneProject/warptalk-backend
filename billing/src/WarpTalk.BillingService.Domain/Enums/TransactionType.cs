// =======================================================
// Domain/Enums/TransactionType.cs
// =======================================================

namespace WarpTalk.BillingService.Domain.Enums;

public enum TransactionType
{
    SubscriptionPurchase = 1,

    SubscriptionRenewal = 2,

    CreditTopUp = 3,

    Refund = 4
}