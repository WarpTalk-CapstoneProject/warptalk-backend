using System;
using WarpTalk.BillingService.Domain.Constants;

namespace WarpTalk.BillingService.Domain.Entities;

/// <summary>
/// G11 — subscription.workspace_addons: one workspace's live add-on subscription. It is a
/// separate Stripe subscription from the plan's, so cancelling an add-on never touches the plan
/// and a plan change never silently drops an add-on.
/// </summary>
public class WorkspaceAddon
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid AddonId { get; set; }
    public Guid UserId { get; set; }
    public int Quantity { get; set; }
    public string BillingCycle { get; set; } = SubscriptionConstants.BillingCycles.Monthly;
    public string Currency { get; set; } = PackageCatalogConstants.Currencies.Vnd;

    /// <summary>List price of one unit per cycle when bought (before any coupon).</summary>
    public decimal UnitPrice { get; set; }

    /// <summary>Everything actually paid for this add-on so far: first invoice plus renewals.</summary>
    public decimal AmountBilledTotal { get; set; }

    /// <summary>One of <see cref="PackageCatalogConstants.WorkspaceAddonStatuses"/>.</summary>
    public string Status { get; set; } = PackageCatalogConstants.WorkspaceAddonStatuses.Active;

    public string? StripeSubscriptionId { get; set; }
    public string? StripeSessionId { get; set; }
    public Guid? CouponId { get; set; }
    public DateTime? CurrentPeriodEnd { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CancelledAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public virtual Addon? Addon { get; set; }

    /// <summary>
    /// Whether the add-on's entitlement is in force at <paramref name="nowUtc"/>. A cancelling
    /// add-on keeps granting until its paid period ends, as a cancelled plan does.
    /// </summary>
    public bool GrantsAt(DateTime nowUtc) =>
        Status == PackageCatalogConstants.WorkspaceAddonStatuses.Active
        || (Status == PackageCatalogConstants.WorkspaceAddonStatuses.Cancelling
            && (CurrentPeriodEnd is null || CurrentPeriodEnd > nowUtc));
}
