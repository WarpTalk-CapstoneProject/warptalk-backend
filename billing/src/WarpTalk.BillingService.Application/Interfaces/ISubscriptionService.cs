using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Interfaces;

public interface ISubscriptionService
{
    Task<Result<SubscriptionDto>> GetActiveSubscriptionAsync(Guid workspaceId, CancellationToken cancellationToken = default);
    Task<Result<PaginatedResponse<SubscriptionDto>>> GetGlobalSubscriptionsAsync(PaginationQuery query, CancellationToken cancellationToken = default);
    Task<Result<SubscriptionDto>> CreateWorkspaceContractSubscriptionAsync(CreateWorkspaceContractSubscriptionRequest request, CancellationToken cancellationToken = default);
    Task<Result<SubscriptionDto>> CreateTrialSubscriptionAsync(TrialSubscriptionRequest request, CancellationToken cancellationToken = default);
    Task<Result<bool>> CancelSubscriptionAsync(Guid workspaceId, string? reason, CancellationToken cancellationToken = default);
    Task<Result<SubscriptionDto>> ResumeSubscriptionAsync(Guid workspaceId, ResumeSubscriptionRequest request, CancellationToken cancellationToken = default);
    Task<Result<SubscriptionDto>> UpdateContractTermsAsync(Guid workspaceId, UpdateSubscriptionContractTermsRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Platform-admin plan swap on a live subscription. Moves the catalogue row the subscription
    /// points at and republishes entitlements; the credit balance is deliberately untouched —
    /// compensation, when owed, is an explicit AdjustWorkspaceCreditsAsync with its own audit row,
    /// not a side effect hidden inside a swap.
    /// </summary>
    Task<Result<SubscriptionDto>> AdminChangePlanAsync(Guid workspaceId, Guid planId, Guid adminUserId, CancellationToken cancellationToken = default);
    Task<Result<WorkspaceOverageSettingDto>> GetOverageSettingAsync(Guid workspaceId, CancellationToken cancellationToken = default);
    Task<Result<WorkspaceOverageSettingDto>> SetOverageAsync(Guid workspaceId, SetWorkspaceOverageRequest request, CancellationToken cancellationToken = default);
}
