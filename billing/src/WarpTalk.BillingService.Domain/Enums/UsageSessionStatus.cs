// =======================================================
// Domain/Enums/UsageSessionStatus.cs
// =======================================================

namespace WarpTalk.BillingService.Domain.Enums;

public enum UsageSessionStatus
{
    Active = 1,

    Paused = 2,

    Ended = 3,

    Failed = 4
}