using System;
using WarpTalk.BillingService.Domain.Enums;

namespace WarpTalk.BillingService.Domain.Entities;

public class Transaction
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public long OrderCode { get; set; }
    
    public Guid WorkspaceId { get; set; }
    
    public decimal AmountVnd { get; set; }
    public decimal PurchasedMinutes { get; set; }
    
    public TransactionStatus Status { get; set; } = TransactionStatus.Pending;
    public string? PayOsTransactionId { get; set; }
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}
