using System;
using WarpTalk.BillingService.Domain.Constants;

namespace WarpTalk.BillingService.Domain.Entities;

public partial class Subscription
{
    /// <summary>
    /// Whether this subscription puts its PLAN'S NUMBERS IN FORCE right now.
    ///
    /// WT-430. This test existed twice, spelled out identically in EntitlementResolver.GatherAsync
    /// and GrpcBillingMapper.ToFeatureAccessResponse, with a comment in one asking the reader to
    /// keep them in step by hand. They are the same question and now have one answer.
    ///
    /// All three conditions are load-bearing, and production proved it: a demo workspace carried
    /// <c>is_active = true</c>, a period ending three weeks out, and <c>status = 'cancelled'</c>.
    /// Exactly one of the three failed, so every entitlement fell to the platform floor — 5 rooms,
    /// 2 participants, voice clone and the assistant off — under an Enterprise label. Testing any
    /// two of the three would have called that subscription live.
    ///
    /// NOT the same question as "which subscription do I bill or credit". SubscriptionService's
    /// LoadActiveSubscriptionAsync deliberately asks a broader one — a cancelled subscription still
    /// inside its paid period keeps its credits until the period ends — and is left alone on
    /// purpose. Entitlements and money are different questions with different answers.
    /// </summary>
    public bool GrantsPlanEntitlements(DateTime nowUtc) =>
        IsActive
        && Status == SubscriptionConstants.SubscriptionStatuses.Active
        && CurrentPeriodEnd >= nowUtc;
}
