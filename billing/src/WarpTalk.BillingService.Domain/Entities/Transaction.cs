// =======================================================
// Domain/Entities/Transaction.cs
// =======================================================

using WarpTalk.BillingService.Domain.Enums;

namespace WarpTalk.BillingService.Domain.Entities;

public class Transaction
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WorkspaceId { get; set; }

    public Guid? OwnerUserId { get; set; }

    public Guid? SubscriptionId { get; set; }

    public Guid? PlanId { get; set; }

    public TransactionType Type { get; set; }

    public long OrderCode { get; set; }

    public decimal AmountVnd { get; set; }

    public TransactionStatus Status { get; set; }

    public string PaymentProvider { get; set; }
        = "PayOS";

    public string? PayOsTransactionId { get; set; }

    public string IdempotencyKey { get; set; }
        = string.Empty;

    public string? FailureReason { get; set; }

    public DateTime CreatedAt { get; set; }
        = DateTime.UtcNow;

    public DateTime? CompletedAt { get; set; }
    public decimal PurchasedCredits { get; set; }
}