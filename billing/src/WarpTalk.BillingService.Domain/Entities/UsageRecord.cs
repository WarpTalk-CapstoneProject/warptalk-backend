using System;

namespace WarpTalk.BillingService.Domain.Entities;

public partial class UsageRecord
{
    public Guid Id { get; set; }

    public Guid SubscriptionId { get; set; }

    public Guid UserId { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid? TranslationRoomId { get; set; }

    public Guid? SegmentId { get; set; }

    public string UsageType { get; set; } = null!;

    public string Unit { get; set; } = null!;

    public decimal Quantity { get; set; }

    public int CreditsConsumed { get; set; }

    public int? DurationSeconds { get; set; }

    /// <summary>
    /// JSONB column for arbitrary usage details.
    /// </summary>
    public string? Details { get; set; }

    public DateTime RecordedAt { get; set; }

    public virtual Subscription Subscription { get; set; } = null!;
}
