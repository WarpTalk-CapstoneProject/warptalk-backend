using System;
using WarpTalk.BillingService.Domain.Enums;

namespace WarpTalk.BillingService.Domain.Entities;

public partial class Transaction
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid? SubscriptionId { get; set; }

    public decimal Amount { get; set; }

    public TransactionStatus Status { get; set; }

    public string? ExternalId { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual Subscription? Subscription { get; set; }
}
