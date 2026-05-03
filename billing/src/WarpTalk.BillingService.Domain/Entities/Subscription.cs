// =======================================================
// Domain/Entities/Subscription.cs
// =======================================================

using WarpTalk.BillingService.Domain.Enums;

namespace WarpTalk.BillingService.Domain.Entities;

public class Subscription
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WorkspaceId { get; set; }

    /// <summary>
    /// Workspace owner / billing owner
    /// </summary>
    public Guid OwnerUserId { get; set; }

    public Guid PlanId { get; set; }

    public SubscriptionStatus Status { get; set; }
        = SubscriptionStatus.Active;

    /// <summary>
    /// Snapshot pricing to avoid future plan changes affecting old subscriptions
    /// </summary>
    public decimal SnapshotMonthlyPriceVnd { get; set; }

    public decimal SnapshotIncludedCredits { get; set; }

    public bool AutoRenew { get; set; } = true;

    public DateTime StartDate { get; set; }
        = DateTime.UtcNow;

    public DateTime? EndDate { get; set; }
    public DateTime? CancelledAt { get; set; }

    public DateTime CreatedAt { get; set; }
        = DateTime.UtcNow;

    public DateTime? UpdatedAt { get; set; }
}