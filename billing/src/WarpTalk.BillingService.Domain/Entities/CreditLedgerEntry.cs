// =======================================================
// Domain/Entities/CreditLedgerEntry.cs
// =======================================================

using WarpTalk.BillingService.Domain.Enums;

namespace WarpTalk.BillingService.Domain.Entities;

public class CreditLedgerEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WorkspaceId { get; set; }

    public Guid? UserId { get; set; }

    public Guid? SubscriptionId { get; set; }

    public Guid? MeetingId { get; set; }

    public Guid? TransactionId { get; set; }

    public LedgerEntryType Type { get; set; }

    public BillingFeatureType FeatureType { get; set; }

    /// <summary>
    /// Positive = add credits
    /// Negative = deduct credits
    /// </summary>
    public decimal Amount { get; set; }

    /// <summary>
    /// Optional snapshot for fast reads
    /// Source of truth is still SUM(ledger entries)
    /// </summary>
    public decimal? BalanceAfter { get; set; }

    public string Currency { get; set; }
        = "CREDIT";

    /// <summary>
    /// Prevent duplicate billing
    /// </summary>
    public string IdempotencyKey { get; set; }
        = string.Empty;

    public string? MetadataJson { get; set; }

    public DateTime CreatedAt { get; set; }
        = DateTime.UtcNow;
}