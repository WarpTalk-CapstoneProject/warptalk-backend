using Microsoft.EntityFrameworkCore;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Enums;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Services;

/// <summary>
/// Billing service implementation with subscription, token, and transaction management.
/// Includes try-catch-result pattern, parameter validation, and subscription tier support.
/// </summary>
public class BillingServices : IBillingService
{
    private readonly IUnitOfWork _uow;

    public BillingServices(IUnitOfWork uow)
    {
        _uow = uow;
    }

    /// <summary>Get all active billing plans</summary>
    public async Task<Result<IReadOnlyList<PlanDto>>> GetPlansAsync(CancellationToken ct = default)
    {
        try
        {
            var plans = await _uow.PlansRepository.FindAsync(p => p.Name != null, "", ct);
            
            var payload = plans
                .Select(p => new PlanDto(p.Id, p.Name, p.PricePerMonth, p.TokensPerMonth, true, null))
                .OrderBy(p => p.PricePerMonth)
                .ToList();

            return Result.Success<IReadOnlyList<PlanDto>>(payload);
        }
        catch (Exception)
        {
            return Result.Failure<IReadOnlyList<PlanDto>>("Failed to fetch plans", BillingErrorCodes.SERVICE_UNAVAILABLE);
        }
    }

    /// <summary>Create a new subscription for workspace with plan validation</summary>
    public async Task<Result<SubscriptionDto>> CreateSubscriptionAsync(Guid workspaceId, Guid planId, string duration = "1mo", string tier = "Premium", CancellationToken ct = default)
    {
        // Parameter validation
        if (workspaceId == Guid.Empty)
            return Result.Failure<SubscriptionDto>("Workspace ID cannot be empty", BillingErrorCodes.INVALID_WORKSPACE_ID);
        if (planId == Guid.Empty)
            return Result.Failure<SubscriptionDto>("Plan ID cannot be empty", BillingErrorCodes.PLAN_NOT_FOUND);
        if (!new[] { "1mo", "6mo", "1yr" }.Contains(duration))
            return Result.Failure<SubscriptionDto>("Invalid duration. Allowed: 1mo, 6mo, 1yr", BillingErrorCodes.VALIDATION_FAILED);
        if (!new[] { "Premium", "Enterprise" }.Contains(tier))
            return Result.Failure<SubscriptionDto>("Invalid tier. Allowed: Premium, Enterprise", BillingErrorCodes.VALIDATION_FAILED);

        try
        {
            // Check for existing active subscription
            var existingActive = await _uow.SubscriptionsRepository
                .FirstOrDefaultAsync(s => s.WorkspaceId == workspaceId && s.Status == SubscriptionStatus.Active, "", ct);

            if (existingActive is not null)
                return Result.Failure<SubscriptionDto>("Workspace already has an active subscription", BillingErrorCodes.SUBSCRIPTION_ALREADY_ACTIVE);

            // Verify plan exists
            var plan = await _uow.PlansRepository.GetByIdAsync(planId, ct);
            if (plan is null)
                return Result.Failure<SubscriptionDto>("Plan not found", BillingErrorCodes.PLAN_NOT_FOUND);

            // Calculate end date based on duration
            var endDate = duration switch
            {
                "1mo" => DateTime.UtcNow.AddMonths(1),
                "6mo" => DateTime.UtcNow.AddMonths(6),
                "1yr" => DateTime.UtcNow.AddYears(1),
                _ => DateTime.UtcNow.AddMonths(1)
            };

            // Create subscription
            var subscription = new Subscription
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId,
                PlanId = planId,
                Status = SubscriptionStatus.Active,
                CurrentTokens = plan.TokensPerMonth,
                StartDate = DateTime.UtcNow,
                EndDate = endDate,
                Duration = duration,
                Tier = tier,
                CreatedAt = DateTime.UtcNow
            };

            await _uow.SubscriptionsRepository.AddAsync(subscription, ct);
            await _uow.SaveChangesAsync(ct);

            return Result.Success(MapToDto(subscription));
        }
        catch (Exception)
        {
            return Result.Failure<SubscriptionDto>("Failed to create subscription", BillingErrorCodes.SERVICE_UNAVAILABLE);
        }
    }

    /// <summary>Get active subscription for workspace with null check</summary>
    public async Task<Result<SubscriptionDto>> GetActiveSubscriptionAsync(Guid workspaceId, CancellationToken ct = default)
    {
        if (workspaceId == Guid.Empty)
            return Result.Failure<SubscriptionDto>("Workspace ID cannot be empty", BillingErrorCodes.INVALID_WORKSPACE_ID);

        try
        {
            var subscription = await _uow.SubscriptionsRepository
                .FirstOrDefaultAsync(s => s.WorkspaceId == workspaceId && s.Status == SubscriptionStatus.Active, "", ct);

            if (subscription is null)
                return Result.Failure<SubscriptionDto>("No active subscription found", BillingErrorCodes.SUBSCRIPTION_NOT_FOUND);

            return Result.Success(MapToDto(subscription));
        }
        catch (Exception)
        {
            return Result.Failure<SubscriptionDto>("Failed to fetch active subscription", BillingErrorCodes.SERVICE_UNAVAILABLE);
        }
    }

    /// <summary>Get current token balance - returns tokens available for workspace</summary>
    public async Task<Result<WorkspaceTokensDto>> GetWorkspaceTokensAsync(Guid workspaceId, CancellationToken ct = default)
    {
        if (workspaceId == Guid.Empty)
            return Result.Failure<WorkspaceTokensDto>("Workspace ID cannot be empty", BillingErrorCodes.INVALID_WORKSPACE_ID);

        try
        {
            var activeSubscription = await _uow.SubscriptionsRepository
                .FirstOrDefaultAsync(s => s.WorkspaceId == workspaceId && s.Status == SubscriptionStatus.Active, "", ct);

            if (activeSubscription is null)
                return Result.Failure<WorkspaceTokensDto>("No active subscription found", BillingErrorCodes.SUBSCRIPTION_NOT_FOUND);

            var payload = new WorkspaceTokensDto(
                activeSubscription.WorkspaceId,
                activeSubscription.CurrentTokens,
                activeSubscription.EndDate,
                activeSubscription.Status.ToString().ToLowerInvariant());

            return Result.Success(payload);
        }
        catch (Exception)
        {
            return Result.Failure<WorkspaceTokensDto>("Failed to fetch workspace tokens", BillingErrorCodes.SERVICE_UNAVAILABLE);
        }
    }

    /// <summary>Cancel active subscription for workspace</summary>
    public async Task<Result<string>> CancelSubscriptionAsync(Guid workspaceId, string? reason = null, CancellationToken ct = default)
    {
        if (workspaceId == Guid.Empty)
            return Result.Failure<string>("Workspace ID cannot be empty", BillingErrorCodes.INVALID_WORKSPACE_ID);

        try
        {
            var subscription = await _uow.SubscriptionsRepository
                .FirstOrDefaultAsync(s => s.WorkspaceId == workspaceId && s.Status == SubscriptionStatus.Active, "", ct);

            if (subscription is null)
                return Result.Failure<string>("No active subscription found", BillingErrorCodes.SUBSCRIPTION_NOT_FOUND);

            subscription.Status = SubscriptionStatus.Cancelled;
            subscription.EndDate = DateTime.UtcNow;

            _uow.SubscriptionsRepository.Update(subscription);
            await _uow.SaveChangesAsync(ct);

            return Result.Success("SUBSCRIPTION_CANCELLED");
        }
        catch (Exception)
        {
            return Result.Failure<string>("Failed to cancel subscription", BillingErrorCodes.SERVICE_UNAVAILABLE);
        }
    }

    /// <summary>Top-up tokens for active subscription - returns updated token count</summary>
    public async Task<Result<int>> TopUpTokensAsync(Guid workspaceId, int amount, string? referenceType = null, Guid? referenceId = null, CancellationToken ct = default)
    {
        if (workspaceId == Guid.Empty)
            return Result.Failure<int>("Workspace ID cannot be empty", BillingErrorCodes.INVALID_WORKSPACE_ID);
        if (amount <= 0)
            return Result.Failure<int>("Amount must be greater than 0", BillingErrorCodes.INVALID_AMOUNT);

        try
        {
            var subscription = await _uow.SubscriptionsRepository
                .FirstOrDefaultAsync(s => s.WorkspaceId == workspaceId && s.Status == SubscriptionStatus.Active, "", ct);

            if (subscription is null)
                return Result.Failure<int>("No active subscription found", BillingErrorCodes.SUBSCRIPTION_NOT_FOUND);

            subscription.CurrentTokens += amount;
            _uow.SubscriptionsRepository.Update(subscription);

            // Record token transaction
            var tokenTx = new TokenTransaction
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId,
                Amount = amount,
                Type = TokenTransactionType.TopUp,
                ReferenceId = referenceId,
                ReferenceType = referenceType ?? "topup",
                CreatedBy = "system",
                CreatedAt = DateTime.UtcNow
            };

            await _uow.TokenTransactionsRepository.AddAsync(tokenTx, ct);
            await _uow.SaveChangesAsync(ct);

            return Result.Success(subscription.CurrentTokens);
        }
        catch (Exception)
        {
            return Result.Failure<int>("Failed to top-up tokens", BillingErrorCodes.SERVICE_UNAVAILABLE);
        }
    }

    /// <summary>Consume tokens for service usage - returns remaining tokens</summary>
    public async Task<Result<int>> ConsumeTokensAsync(Guid workspaceId, int amount, string referenceType, Guid? referenceId = null, CancellationToken ct = default)
    {
        if (workspaceId == Guid.Empty)
            return Result.Failure<int>("Workspace ID cannot be empty", BillingErrorCodes.INVALID_WORKSPACE_ID);
        if (amount <= 0)
            return Result.Failure<int>("Amount must be greater than 0", BillingErrorCodes.INVALID_AMOUNT);
        if (string.IsNullOrWhiteSpace(referenceType))
            return Result.Failure<int>("Reference type is required", BillingErrorCodes.VALIDATION_FAILED);

        try
        {
            var subscription = await _uow.SubscriptionsRepository
                .FirstOrDefaultAsync(s => s.WorkspaceId == workspaceId && s.Status == SubscriptionStatus.Active, "", ct);

            if (subscription is null)
                return Result.Failure<int>("No active subscription found", BillingErrorCodes.SUBSCRIPTION_NOT_FOUND);

            if (subscription.CurrentTokens < amount)
                return Result.Failure<int>($"Insufficient tokens. Available: {subscription.CurrentTokens}, Required: {amount}", BillingErrorCodes.INSUFFICIENT_CREDITS);

            subscription.CurrentTokens -= amount;
            _uow.SubscriptionsRepository.Update(subscription);

            // Record token transaction
            var tokenTx = new TokenTransaction
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId,
                Amount = -amount,
                Type = TokenTransactionType.Consume,
                ReferenceId = referenceId,
                ReferenceType = referenceType,
                CreatedBy = "system",
                CreatedAt = DateTime.UtcNow
            };

            await _uow.TokenTransactionsRepository.AddAsync(tokenTx, ct);
            await _uow.SaveChangesAsync(ct);

            return Result.Success(subscription.CurrentTokens);
        }
        catch (Exception)
        {
            return Result.Failure<int>("Failed to consume tokens", BillingErrorCodes.SERVICE_UNAVAILABLE);
        }
    }

    /// <summary>Get token transaction history with pagination</summary>
    public async Task<Result<PaginatedResponse<TokenTransactionDto>>> GetTokenHistoryAsync(Guid workspaceId, int pageNumber = 1, int pageSize = 50, CancellationToken ct = default)
    {
        if (workspaceId == Guid.Empty)
            return Result.Failure<PaginatedResponse<TokenTransactionDto>>("Workspace ID cannot be empty", BillingErrorCodes.INVALID_WORKSPACE_ID);
        if (pageNumber < 1 || pageSize < 1 || pageSize > 200)
            return Result.Failure<PaginatedResponse<TokenTransactionDto>>("Invalid pagination parameters", BillingErrorCodes.VALIDATION_FAILED);

        try
        {
            var query = _uow.TokenTransactionsRepository.Query()
                .Where(t => t.WorkspaceId == workspaceId)
                .OrderByDescending(t => t.CreatedAt);

            var totalCount = await query.CountAsync(ct);
            var items = await query
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .Select(t => new TokenTransactionDto(
                    t.Id, t.WorkspaceId, t.Amount, t.Type.ToString(),
                    t.ReferenceId, t.ReferenceType, t.CreatedBy, t.CreatedAt))
                .ToListAsync(ct);

            var totalPages = (int)Math.Ceiling((double)totalCount / pageSize);
            var response = new PaginatedResponse<TokenTransactionDto>(items, pageNumber, pageSize, totalCount, totalPages);
            return Result.Success(response);
        }
        catch (Exception)
        {
            return Result.Failure<PaginatedResponse<TokenTransactionDto>>("Failed to fetch token history", BillingErrorCodes.SERVICE_UNAVAILABLE);
        }
    }

    /// <summary>Get transaction history with pagination</summary>
    public async Task<Result<PaginatedResponse<TransactionDto>>> GetTransactionHistoryAsync(Guid workspaceId, int pageNumber = 1, int pageSize = 50, CancellationToken ct = default)
    {
        if (workspaceId == Guid.Empty)
            return Result.Failure<PaginatedResponse<TransactionDto>>("Workspace ID cannot be empty", BillingErrorCodes.INVALID_WORKSPACE_ID);
        if (pageNumber < 1 || pageSize < 1 || pageSize > 200)
            return Result.Failure<PaginatedResponse<TransactionDto>>("Invalid pagination parameters", BillingErrorCodes.VALIDATION_FAILED);

        try
        {
            var query = _uow.TransactionsRepository.Query()
                .Where(t => t.WorkspaceId == workspaceId)
                .OrderByDescending(t => t.CreatedAt);

            var totalCount = await query.CountAsync(ct);
            var items = await query
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .Select(t => new TransactionDto(
                    t.Id, t.WorkspaceId, t.SubscriptionId, t.Amount,
                    t.Status.ToString(), t.ExternalId, t.CreatedBy, t.CreatedAt))
                .ToListAsync(ct);

            var totalPages = (int)Math.Ceiling((double)totalCount / pageSize);
            var response = new PaginatedResponse<TransactionDto>(items, pageNumber, pageSize, totalCount, totalPages);
            return Result.Success(response);
        }
        catch (Exception)
        {
            return Result.Failure<PaginatedResponse<TransactionDto>>("Failed to fetch transaction history", BillingErrorCodes.SERVICE_UNAVAILABLE);
        }
    }

    /// <summary>Map subscription entity to DTO</summary>
    private SubscriptionDto MapToDto(Subscription subscription) =>
        new(subscription.Id, subscription.WorkspaceId, subscription.PlanId,
            subscription.Status.ToString(), subscription.CurrentTokens,
            subscription.StartDate, subscription.EndDate,
            subscription.Duration, subscription.Tier, subscription.CreatedAt);
}

// Backward-compatibility adapter while centralizing real values in WarpTalk.Shared.ErrorCodes.
internal static class BillingErrorCodes
{
    public const string SUBSCRIPTION_NOT_FOUND = ErrorCodes.BillingSubscriptionNotFound;
    public const string INSUFFICIENT_CREDITS = ErrorCodes.BillingInsufficientCredits;
    public const string SUBSCRIPTION_ALREADY_ACTIVE = ErrorCodes.BillingSubscriptionAlreadyActive;
    public const string PLAN_NOT_FOUND = ErrorCodes.BillingPlanNotFound;
    public const string VALIDATION_FAILED = ErrorCodes.BillingValidationFailed;
    public const string SERVICE_UNAVAILABLE = ErrorCodes.BillingServiceUnavailable;
    public const string INVALID_WORKSPACE_ID = ErrorCodes.BillingValidationFailed;
    public const string INVALID_AMOUNT = ErrorCodes.BillingValidationFailed;
}
