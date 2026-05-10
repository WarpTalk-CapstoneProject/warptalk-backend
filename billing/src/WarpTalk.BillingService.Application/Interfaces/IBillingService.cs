using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Interfaces;

/// <summary>
/// Billing service interface for subscription, token, and transaction management.
/// </summary>
public interface IBillingService
{
    // --- PLANS ---
    /// <summary>Get all active billing plans</summary>
    Task<Result<IReadOnlyList<PlanDto>>> GetPlansAsync(CancellationToken ct = default);

    // --- SUBSCRIPTIONS ---
    /// <summary>Create a new subscription for workspace</summary>
    Task<Result<SubscriptionDto>> CreateSubscriptionAsync(Guid workspaceId, Guid planId, string duration = "1mo", string tier = "Premium", CancellationToken ct = default);

    /// <summary>Get active subscription for workspace</summary>
    Task<Result<SubscriptionDto>> GetActiveSubscriptionAsync(Guid workspaceId, CancellationToken ct = default);

    /// <summary>Get current token balance for workspace</summary>
    Task<Result<WorkspaceTokensDto>> GetWorkspaceTokensAsync(Guid workspaceId, CancellationToken ct = default);

    /// <summary>Cancel workspace subscription - returns only status code</summary>
    Task<Result<string>> CancelSubscriptionAsync(Guid workspaceId, string? reason = null, CancellationToken ct = default);

    // --- TOKEN MANAGEMENT ---
    /// <summary>Top-up tokens - returns new token count</summary>
    Task<Result<int>> TopUpTokensAsync(Guid workspaceId, int amount, string? referenceType = null, Guid? referenceId = null, CancellationToken ct = default);

    /// <summary>Consume tokens for service usage</summary>
    Task<Result<int>> ConsumeTokensAsync(Guid workspaceId, int amount, string referenceType, Guid? referenceId = null, CancellationToken ct = default);

    /// <summary>Get token transaction history</summary>
    Task<Result<PaginatedResponse<TokenTransactionDto>>> GetTokenHistoryAsync(Guid workspaceId, int pageNumber = 1, int pageSize = 50, CancellationToken ct = default);

    // --- TRANSACTIONS ---
    /// <summary>Get payment transaction history</summary>
    Task<Result<PaginatedResponse<TransactionDto>>> GetTransactionHistoryAsync(Guid workspaceId, int pageNumber = 1, int pageSize = 50, CancellationToken ct = default);
}
