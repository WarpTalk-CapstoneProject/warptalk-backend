// =======================================================
// Domain/Enums/UsageEventStatus.cs
// =======================================================

namespace WarpTalk.BillingService.Domain.Enums;

public enum UsageEventStatus
{
    Pending = 0,

    Processing = 1,

    Processed = 2,

    Failed = 3,

    Rejected = 4
}