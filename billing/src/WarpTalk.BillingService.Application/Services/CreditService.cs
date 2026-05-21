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
    private readonly IBillingMessagePublisher _messagePublisher;

    public CreditService(IUnitOfWork unitOfWork, ILogger<CreditService> logger, IBillingMessagePublisher messagePublisher)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
        _messagePublisher = messagePublisher;
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

    public async Task<Result<CreditBalanceDto>> RecordUsageAsync(
        RecordUsageRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var sub = await _unitOfWork.SubscriptionRepository.FirstOrDefaultAsync(
                s => s.WorkspaceId == request.HostWorkspaceId && s.IsActive && s.DeletedAt == null,
                cancellationToken);

            if (sub is null)
                return Result.Failure<CreditBalanceDto>(
                    "No active subscription found for the host workspace.",
                    ErrorCodes.BillingSubscriptionNotFound);

            if (sub.CreditsRemaining < request.CreditsConsumed)
                return Result.Failure<CreditBalanceDto>(
                    "Insufficient credits in the host workspace.",
                    ErrorCodes.BillingInsufficientCredits);

            // Deduct credits
            sub.CreditsRemaining -= request.CreditsConsumed;
            sub.CreditsUsedThisCycle += request.CreditsConsumed;
            sub.UpdatedAt = DateTime.UtcNow;
            _unitOfWork.SubscriptionRepository.Update(sub);

            // 1. Create Transaction (Accounting)
            var tx = new CreditTransaction
            {
                Id = Guid.NewGuid(),
                SubscriptionId = sub.Id,
                Amount = -request.CreditsConsumed,
                Type = "usage",
                Description = $"AI Usage: {request.UsageType} by User {request.UserId}",
                ReferenceType = "usage_record",
                BalanceAfter = sub.CreditsRemaining,
                CreatedAt = DateTime.UtcNow
            };
            await _unitOfWork.CreditTransactionRepository.AddAsync(tx, cancellationToken);

            // 2. Create Usage Record (Analytics)
            var usage = new UsageRecord
            {
                Id = Guid.NewGuid(),
                SubscriptionId = sub.Id,
                UserId = request.UserId,
                WorkspaceId = request.HostWorkspaceId,
                TranslationRoomId = request.TranslationRoomId,
                UsageType = request.UsageType,
                Unit = request.Unit,
                Quantity = request.Quantity,
                CreditsConsumed = request.CreditsConsumed,
                DurationSeconds = request.DurationSeconds,
                Details = request.Details,
                RecordedAt = DateTime.UtcNow
            };
            await _unitOfWork.UsageRecordRepository.AddAsync(usage, cancellationToken);

            // Save atomic transaction
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            // Publish Realtime update for the Host
            var msg = new WarpTalk.Shared.Models.RealtimeNotificationMessage
            {
                Id = Guid.NewGuid().ToString(),
                UserId = request.HostWorkspaceId.ToString(), // Assuming WorkspaceId == UserId for personal workspaces, or send to workspace channel
                Type = "billing.credits_updated",
                Title = "Credits Deducted",
                Content = $"Host-pays: {request.CreditsConsumed} credits were deducted for {request.UsageType}.",
                PayloadJson = $"{{\"new_balance\": {sub.CreditsRemaining}}}",
                CreatedAt = DateTime.UtcNow.ToString("O")
            };
            
            try 
            {
                await _messagePublisher.PublishAsync("warptalk:notifications:new", msg, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to publish realtime credit update for WorkspaceId {WorkspaceId}", request.HostWorkspaceId);
            }

            return Result.Success(sub.ToCreditBalanceDto(request.HostWorkspaceId));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error recording usage for HostWorkspaceId {HostWorkspaceId}", request.HostWorkspaceId);
            return Result.Failure<CreditBalanceDto>("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }
}
