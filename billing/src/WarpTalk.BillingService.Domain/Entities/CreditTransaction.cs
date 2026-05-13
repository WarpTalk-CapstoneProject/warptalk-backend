using System;
using WarpTalk.BillingService.Domain.Enums;

namespace WarpTalk.BillingService.Domain.Entities;

public partial class CreditTransaction
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public int Amount { get; set; }

    public CreditTransactionType Type { get; set; }

    public Guid? ReferenceId { get; set; }

    public CreditReferenceType? ReferenceType { get; set; }

    public DateTime CreatedAt { get; set; }
}
