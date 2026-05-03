// =======================================================
// Domain/Enums/AuditAction.cs
// =======================================================

namespace WarpTalk.BillingService.Domain.Enums;

public enum AuditAction
{
    Allocate = 1,
    Deduct = 2,
    Refund = 3,
    Expire = 4,
    TopUp = 5,

    PurchasePlan = 6,
    RenewSubscription = 7,
    CancelSubscription = 8,

    MeetingStarted = 9,
    MeetingEnded = 10,

    FallbackModeActivated = 11,
    LowCreditTriggered = 12,

    ManualAdjustment = 13
}