using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Interfaces;

/// <summary>
/// #466 — the non-money half of a card customer's recurring plan: the auto-renew toggle, the
/// billing page's renewal status, the Stripe billing portal, the subscription webhooks that change
/// renewal (updated / deleted), and the one-off admin backfill that links pre-#466 rows.
/// The money half (invoice.paid / invoice.payment_failed) is a payment event: see
/// SubscriptionPaymentEventHandler.Renewal.
/// </summary>
public interface IStripeSubscriptionLifecycleService
{
    Task<Result<SubscriptionDto>> SetAutoRenewAsync(Guid workspaceId, bool autoRenew, CancellationToken ct = default);

    Task<Result<RecurringBillingStatusDto>> GetRecurringBillingStatusAsync(Guid workspaceId, CancellationToken ct = default);

    Task<Result<BillingPortalDto>> CreateBillingPortalAsync(Guid workspaceId, string? returnPath, CancellationToken ct = default);

    Task<Result> ApplyStripeSubscriptionChangeAsync(StripeSubscriptionChange change, CancellationToken ct = default);

    Task<Result<StripeLinkBackfillResultDto>> BackfillStripeLinksAsync(bool dryRun, int limit, CancellationToken ct = default);
}
