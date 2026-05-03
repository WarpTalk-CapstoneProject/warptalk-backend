// =======================================================
// Domain/Enums/SubscriptionStatus.cs
// =======================================================

namespace WarpTalk.BillingService.Domain.Enums;

public enum SubscriptionStatus
{
    Pending = 0,

    Trialing = 1,

    Active = 2,

    Suspended = 3,

    Cancelled = 4,

    Expired = 5
}