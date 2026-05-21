using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Mappers;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Services;

public class CreditService : ICreditService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<CreditService> _logger;

    public CreditService(IUnitOfWork unitOfWork, ILogger<CreditService> logger)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task<Result<CreditBalanceDto>> GetWorkspaceCreditsAsync(
        Guid workspaceId, CancellationToken cancellationToken = default)
    {
        try
        {
            var sub = await _unitOfWork.SubscriptionRepository.FirstOrDefaultAsync(
                s => s.WorkspaceId == workspaceId && s.IsActive && s.DeletedAt == null,
                cancellationToken);

            if (sub is null)
                return Result.Failure<CreditBalanceDto>(
                    "No active subscription found for this workspace.",
                    ErrorCodes.BillingSubscriptionNotFound);

            return Result.Success(sub.ToCreditBalanceDto(workspaceId));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting workspace credits for WorkspaceId {WorkspaceId}", workspaceId);
            return Result.Failure<CreditBalanceDto>("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result<CreditTransactionDto>> ConsumeCreditsAsync(
        Guid workspaceId, ConsumeCreditsRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var sub = await _unitOfWork.SubscriptionRepository.FirstOrDefaultAsync(
                s => s.WorkspaceId == workspaceId && s.IsActive && s.DeletedAt == null,
                cancellationToken);

            if (sub is null)
                return Result.Failure<CreditTransactionDto>(
                    "No active subscription found for this workspace.",
                    ErrorCodes.BillingSubscriptionNotFound);

            if (sub.CreditsRemaining < request.Amount)
                return Result.Failure<CreditTransactionDto>(
                    "Insufficient credits.",
                    ErrorCodes.BillingInsufficientCredits);

            sub.CreditsRemaining -= request.Amount;
            sub.CreditsUsedThisCycle += request.Amount;
            sub.UpdatedAt = DateTime.UtcNow;
            _unitOfWork.SubscriptionRepository.Update(sub);

            var tx = request.ToEntity(sub);

            await _unitOfWork.CreditTransactionRepository.AddAsync(tx, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            return Result.Success(tx.ToDto());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error consuming credits for WorkspaceId {WorkspaceId}", workspaceId);
            return Result.Failure<CreditTransactionDto>("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result<CreditBalanceDto>> TopUpCreditsAsync(
        Guid workspaceId, TopUpRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var sub = await _unitOfWork.SubscriptionRepository.FirstOrDefaultAsync(
                s => s.WorkspaceId == workspaceId && s.IsActive && s.DeletedAt == null,
                cancellationToken);

            if (sub is null)
                return Result.Failure<CreditBalanceDto>(
                    "No active subscription found for this workspace.",
                    ErrorCodes.BillingSubscriptionNotFound);

            sub.CreditsRemaining += request.Amount;
            sub.UpdatedAt = DateTime.UtcNow;
            _unitOfWork.SubscriptionRepository.Update(sub);

            var tx = request.ToEntity(sub);

            await _unitOfWork.CreditTransactionRepository.AddAsync(tx, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            return Result.Success(sub.ToCreditBalanceDto(workspaceId));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error topping up credits for WorkspaceId {WorkspaceId}", workspaceId);
            return Result.Failure<CreditBalanceDto>("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result<PagedResult<CreditTransactionDto>>> GetCreditHistoryAsync(
        Guid workspaceId, int pageNumber, int pageSize, CancellationToken cancellationToken = default)
    {
        try
        {
            var sub = await _unitOfWork.SubscriptionRepository.FirstOrDefaultAsync(
                s => s.WorkspaceId == workspaceId && s.IsActive && s.DeletedAt == null,
                cancellationToken);

            if (sub is null)
                return Result.Failure<PagedResult<CreditTransactionDto>>(
                    "No active subscription found for this workspace.",
                    ErrorCodes.BillingSubscriptionNotFound);

            var size  = pageSize > 0 ? pageSize : 20;
            var skip  = ((pageNumber > 0 ? pageNumber : 1) - 1) * size;

            var items = await _unitOfWork.CreditTransactionRepository.GetPagedAsync(
                t => t.SubscriptionId == sub.Id,
                skip, size,
                q => q.OrderByDescending(t => t.CreatedAt),
                cancellationToken);

            var total = await _unitOfWork.CreditTransactionRepository.CountAsync(
                t => t.SubscriptionId == sub.Id,
                cancellationToken);

            return Result.Success(new PagedResult<CreditTransactionDto>(
                total,
                items.Select(t => t.ToDto())));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting credit history for WorkspaceId {WorkspaceId}", workspaceId);
            return Result.Failure<PagedResult<CreditTransactionDto>>("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    public async Task TakeSnapshotAsync(Guid subscriptionId, CancellationToken cancellationToken = default)
    {
        try
        {
            var sub = await _unitOfWork.SubscriptionRepository.GetByIdAsync(subscriptionId, cancellationToken)
                ?? throw new InvalidOperationException($"Subscription {subscriptionId} not found.");

            var snapshot = new CreditBalanceSnapshot
            {
                SubscriptionId = subscriptionId,
                CreditsRemaining = sub.CreditsRemaining,
                CreditsUsedThisCycle = sub.CreditsUsedThisCycle,
                SnapshotAt = DateTime.UtcNow
            };

            await _unitOfWork.CreditBalanceSnapshotRepository.AddAsync(snapshot, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error taking snapshot for SubscriptionId {SubscriptionId}", subscriptionId);
            throw;  
    }
}
