using System;
using WarpTalk.BillingService.Domain.Enums;

namespace WarpTalk.BillingService.Domain.Entities;

/// <summary>
/// Details of a payment transaction
/// </summary>
/// <example>
/// {
///   "id": "f0f0f0f0-f0f0-f0f0-f0f0-f0f0f0f0f0f0",
///   "orderCode": 123456,
///   "workspaceId": "77777777-7777-7777-7777-777777777777",
///   "amountVnd": 199000,
///   "purchasedMinutes": 500,
///   "status": "Paid",
///   "createdAt": "2026-04-29T10:00:00Z"
/// }
/// </example>
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
