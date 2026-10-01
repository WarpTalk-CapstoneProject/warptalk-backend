namespace WarpTalk.BillingService.Application.DTOs;

/// <summary>#466: the body of PUT subscriptions/workspace/{id}/auto-renew.</summary>
public sealed record SetAutoRenewRequest(bool AutoRenew);

/// <summary>#466: the body of POST subscriptions/workspace/{id}/billing-portal. A path on our own site.</summary>
public sealed record BillingPortalRequest(string? ReturnPath);

public sealed record BillingPortalDto(string Url);

/// <summary>#466: the card on file — brand and last four digits only, never more.</summary>
public sealed record CardOnFileDto(string? Brand, string Last4, int? ExpMonth, int? ExpYear);

/// <summary>
/// #466: what the billing page shows about renewal. <see cref="RenewalMode"/> says who renews:
/// <c>stripe</c> (the saved card is charged), <c>invoice</c> (an invoice is issued), <c>none</c>
/// (nothing — turning auto-renew on needs a new checkout, <see cref="AutoRenewRequiresCheckout"/>).
/// </summary>
public sealed record RecurringBillingStatusDto(
    Guid WorkspaceId,
    Guid SubscriptionId,
    string RenewalMode,
    bool AutoRenew,
    bool CanToggleAutoRenew,
    bool AutoRenewRequiresCheckout,
    DateTime CurrentPeriodEnd,
    DateTime? NextChargeAt,
    decimal? NextChargeAmount,
    string? NextChargeCurrency,
    CardOnFileDto? Card,
    bool CanManagePaymentMethod,
    bool PaymentFailed,
    DateTime? PaymentFailedAt,
    DateTime? PaymentGraceEndsAt,
    string? PaymentFailureReason,
    string? StripeStatus,
    bool StripeUnavailable);

/// <summary>#466: a Stripe subscription event that changes a row's renewal, not its money.</summary>
public sealed record StripeSubscriptionChange(
    string StripeSubscriptionId,
    string Status,
    bool CancelAtPeriodEnd,
    bool Deleted,
    string? CustomerId,
    string WorkspaceIdStr,
    DateTime? PeriodEnd);

public sealed record StripeLinkBackfillRow(Guid SubscriptionId, Guid WorkspaceId, string Outcome, string? StripeSubscriptionId);

public sealed record StripeLinkBackfillResultDto(
    bool DryRun,
    int Examined,
    int Linked,
    int OneOff,
    int Skipped,
    IReadOnlyList<StripeLinkBackfillRow> Rows);
