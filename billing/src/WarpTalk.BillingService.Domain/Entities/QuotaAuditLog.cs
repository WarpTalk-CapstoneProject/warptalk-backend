using System;
using WarpTalk.BillingService.Domain.Enums;

namespace WarpTalk.BillingService.Domain.Entities;

public class QuotaAuditLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    
    public AuditAction Action { get; set; }
    public decimal Amount { get; set; }
    public decimal BalanceAfter { get; set; }
    
    /// <summary>
    /// Serves as the Idempotency Key (SessionId, TurnId, or TransactionId)
    /// </summary>
    public string ReferenceId { get; set; } = string.Empty;
    
    public string? Description { get; set; }
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
