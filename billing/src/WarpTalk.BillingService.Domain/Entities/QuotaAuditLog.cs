using System;
using WarpTalk.BillingService.Domain.Enums;

namespace WarpTalk.BillingService.Domain.Entities;

/// <summary>
/// Audit log for quota changes (Deduction, Refund, Reset, etc.)
/// </summary>
/// <example>
/// {
///   "id": "e0e0e0e0-e0e0-e0e0-e0e0-e0e0e0e0e0e0",
///   "workspaceId": "77777777-7777-7777-7777-777777777777",
///   "action": "Deduction",
///   "amount": 15.5,
///   "balanceAfter": 484.5,
///   "referenceId": "a0a0a0a0-b1b1-c2c2-d3d3-e4e4e4e4e4e4",
///   "description": "Meeting: Weekly Sync",
///   "createdAt": "2026-04-29T11:00:00Z"
/// }
/// </example>
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
