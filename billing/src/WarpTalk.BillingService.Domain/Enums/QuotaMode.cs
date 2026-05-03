// =======================================================
// Domain/Enums/QuotaMode.cs
// =======================================================

namespace WarpTalk.BillingService.Domain.Enums;

public enum QuotaMode
{
    FullVoice = 1,

    TextOnly = 2,

    Restricted = 3,

    Exhausted = 4
}