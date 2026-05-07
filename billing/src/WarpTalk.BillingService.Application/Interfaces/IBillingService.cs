using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Interfaces;

/// <summary>
/// Billing service interface for subscription, credit, and transaction management.
/// </summary>
public interface IBillingService
{
    // --- PLANS ---
    /// <summary>Get all active billing plans</summary>
    Task<Result<IReadOnlyList<PlanDto>>> GetPlansAsync(CancellationToken ct = default);

    // --- SUBSCRIPTIONS ---
    /// <summary>Create a new subscription for workspace</summary>
    Task<Result<SubscriptionDto>> CreateSubscriptionAsync(Guid workspaceId, Guid planId, CancellationToken ct = default);

    /// <summary>Get active subscription for workspace</summary>
    Task<Result<SubscriptionDto>> GetActiveSubscriptionAsync(Guid workspaceId, CancellationToken ct = default);

    /// <summary>Get current credits balance for workspace</summary>
    Task<Result<WorkspaceCreditsDto>> GetWorkspaceCreditsAsync(Guid workspaceId, CancellationToken ct = default);

    /// <summary>Cancel workspace subscription</summary>
    Task<Result<WorkspaceCreditsDto>> CancelSubscriptionAsync(Guid workspaceId, string? reason = null, CancellationToken ct = default);

    // --- CREDIT MANAGEMENT ---
    /// <summary>Top-up credits (admin only or via payment)</summary>
    Task<Result<WorkspaceCreditsDto>> TopUpCreditsAsync(Guid workspaceId, int amount, string referenceType = "topup", Guid? referenceId = null, CancellationToken ct = default);

    /// <summary>Consume credits for service usage</summary>
    Task<Result<WorkspaceCreditsDto>> ConsumeCreditsAsync(Guid workspaceId, int amount, string referenceType, Guid? referenceId = null, CancellationToken ct = default);

    /// <summary>Get credit transaction history</summary>
    Task<Result<PaginatedResponse<CreditTransactionDto>>> GetCreditHistoryAsync(Guid workspaceId, int pageNumber = 1, int pageSize = 50, CancellationToken ct = default);

    // --- TRANSACTIONS ---
    /// <summary>Get payment transaction history</summary>
    Task<Result<PaginatedResponse<TransactionDto>>> GetTransactionHistoryAsync(Guid workspaceId, int pageNumber = 1, int pageSize = 50, CancellationToken ct = default);
}
