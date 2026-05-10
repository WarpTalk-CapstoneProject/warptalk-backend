using System;
using WarpTalk.BillingService.Domain.Enums;

namespace WarpTalk.BillingService.Domain.Entities;

public partial class TokenTransaction
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public int Amount { get; set; }

    public TokenTransactionType Type { get; set; }

    public Guid? ReferenceId { get; set; }

    public string? ReferenceType { get; set; }

    public string? CreatedBy { get; set; }

    public DateTime CreatedAt { get; set; }
}
