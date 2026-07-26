using System;

namespace WarpTalk.BillingService.Application.DTOs;

public class RedisCreditReservationDto
{
    public string IdempotencyKey { get; set; } = null!;
    public Guid SubscriptionId { get; set; }
    public Guid WorkspaceId { get; set; }
    public int Amount { get; set; }
}

public class TempUsageLogDto
{
    public Guid SubscriptionId { get; set; }
    public string? UserId { get; set; }
    public Guid WorkspaceId { get; set; }
    public string? TranslationRoomId { get; set; }
    public string UsageType { get; set; } = null!;
    public string ChargeType { get; set; } = null!;
    public Guid? ReferenceId { get; set; }
    public string ReferenceType { get; set; } = null!;
    public double Quantity { get; set; }
    public string Unit { get; set; } = null!;
    public int CreditsConsumed { get; set; }
    public Guid? PricingRateCardId { get; set; }
    public decimal? UnitPriceSnapshot { get; set; }
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public Guid? TranscriptSegmentId { get; set; }
    public string IdempotencyKey { get; set; } = null!;
    public string? Details { get; set; }
    public DateTime CreatedAt { get; set; }
}
