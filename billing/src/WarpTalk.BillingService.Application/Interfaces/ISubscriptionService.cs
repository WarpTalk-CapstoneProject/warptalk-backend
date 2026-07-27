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
    Task<Result<SubscriptionDto>> CreateSubscriptionAsync(SubscriptionRequest request, CancellationToken cancellationToken = default);
    Task<Result<SubscriptionDto>> CreateWorkspaceContractSubscriptionAsync(CreateWorkspaceContractSubscriptionRequest request, CancellationToken cancellationToken = default);
    Task<Result<SubscriptionDto>> CreateTrialSubscriptionAsync(TrialSubscriptionRequest request, CancellationToken cancellationToken = default);
    Task<Result<bool>> CancelSubscriptionAsync(Guid workspaceId, string? reason, CancellationToken cancellationToken = default);
    Task<Result<SubscriptionDto>> ChangeSubscriptionAsync(SubscriptionRequest request, CancellationToken cancellationToken = default);
    Task<Result<bool>> ActivateSubscriptionAsync(Guid workspaceId, CancellationToken cancellationToken = default);
    Task<Result<SubscriptionDto>> ResumeSubscriptionAsync(Guid workspaceId, ResumeSubscriptionRequest request, CancellationToken cancellationToken = default);
    Task<Result<SubscriptionDto>> UpdateContractTermsAsync(Guid workspaceId, UpdateSubscriptionContractTermsRequest request, CancellationToken cancellationToken = default);
}
