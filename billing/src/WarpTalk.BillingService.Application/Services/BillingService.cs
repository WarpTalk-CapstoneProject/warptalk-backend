using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Enums;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Services;

/// <summary>
/// Billing service implementation with subscription, credit, and transaction management.
/// Includes structured logging, concurrency handling, and business rule enforcement.
/// </summary>
public class BillingService : IBillingService
{
    private readonly IUnitOfWork _uow;
    private readonly ILogger<BillingService> _logger;
    private const int MaxRetries = 3;

    public BillingService(IUnitOfWork uow, ILogger<BillingService> logger)
    {
        _uow = uow;
        _logger = logger;
    }

    /// <summary>Get all active billing plans</summary>
    public async Task<Result<IReadOnlyList<PlanDto>>> GetPlansAsync(CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation("Fetching all active plans");
            
            var plans = await _uow.Plans
                 .FindAsync(p => p.Name != null, "", ct);
            
            var payload = plans
                .Select(p => new PlanDto(p.Id, p.Name, p.Price, p.CreditsPerMonth, true, null))
                .OrderBy(p => p.Price)
                .ToList();

            _logger.LogInformation("Successfully fetched {PlanCount} plans", payload.Count);
            return Result.Success<IReadOnlyList<PlanDto>>(payload);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching plans");
            return Result.Failure<IReadOnlyList<PlanDto>>("Failed to fetch plans", BillingErrorCodes.SERVICE_UNAVAILABLE);
        }
    }

    /// <summary>Create a new subscription for workspace</summary>
    public async Task<Result<SubscriptionDto>> CreateSubscriptionAsync(Guid workspaceId, Guid planId, CancellationToken ct = default)
    {
        if (workspaceId == Guid.Empty)
            return Result.Failure<SubscriptionDto>("Workspace ID cannot be empty", BillingErrorCodes.INVALID_WORKSPACE_ID);

        try
        {
            _logger.LogInformation("Creating subscription for workspace {WorkspaceId} with plan {PlanId}", workspaceId, planId);

            // Check for existing active subscription
            var existingActive = await _uow.Subscriptions
                    .FirstOrDefaultAsync(
                        s => s.WorkspaceId == workspaceId && s.Status == SubscriptionStatus.Active,
                        "",
                        ct);

            if (existingActive is not null)
            {
                _logger.LogWarning("Subscription already exists for workspace {WorkspaceId}", workspaceId);
                return Result.Failure<SubscriptionDto>(
                    "Workspace already has an active subscription",
                    BillingErrorCodes.SUBSCRIPTION_ALREADY_ACTIVE);
            }

            // Verify plan exists and is active
            var plan = await _uow.Plans.GetByIdAsync(planId, ct);
            if (plan is null)
            {
                _logger.LogWarning("Plan not found: {PlanId}", planId);
                return Result.Failure<SubscriptionDto>("Plan not found", BillingErrorCodes.PLAN_NOT_FOUND);
            }

            // Create subscription
            var subscription = new Subscription
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId,
                PlanId = planId,
                Status = SubscriptionStatus.Active,
                CurrentCredits = plan.CreditsPerMonth,
                StartDate = DateTime.UtcNow,
                EndDate = DateTime.UtcNow.AddMonths(1),
                CreatedAt = DateTime.UtcNow
            };

            await _uow.Subscriptions.AddAsync(subscription, ct);
            await _uow.SaveChangesAsync(ct);

            _logger.LogInformation("Subscription created successfully: {SubscriptionId} for workspace {WorkspaceId}", 
                subscription.Id, workspaceId);

            return Result.Success(MapToDto(subscription));
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Database error creating subscription for workspace {WorkspaceId}", workspaceId);
            return Result.Failure<SubscriptionDto>(
                "Failed to create subscription due to database error",
                BillingErrorCodes.SUBSCRIPTION_CONFLICT);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating subscription for workspace {WorkspaceId}", workspaceId);
            return Result.Failure<SubscriptionDto>("Failed to create subscription", BillingErrorCodes.SERVICE_UNAVAILABLE);
        }
    }

    /// <summary>Get active subscription for workspace</summary>
    public async Task<Result<SubscriptionDto>> GetActiveSubscriptionAsync(Guid workspaceId, CancellationToken ct = default)
    {
        if (workspaceId == Guid.Empty)
            return Result.Failure<SubscriptionDto>("Workspace ID cannot be empty", BillingErrorCodes.INVALID_WORKSPACE_ID);

        try
        {
            _logger.LogInformation("Fetching active subscription for workspace {WorkspaceId}", workspaceId);

            var subscription = await _uow.Subscriptions
                .FirstOrDefaultAsync(
                    s => s.WorkspaceId == workspaceId && s.Status == SubscriptionStatus.Active,
                    "Plan",
                    ct);

            if (subscription is null)
            {
                _logger.LogInformation("No active subscription found for workspace {WorkspaceId}", workspaceId);
                return Result.Failure<SubscriptionDto>(
                    "No active subscription found",
                    BillingErrorCodes.SUBSCRIPTION_NOT_FOUND);
            }

            return Result.Success(MapToDto(subscription));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching subscription for workspace {WorkspaceId}", workspaceId);
            return Result.Failure<SubscriptionDto>("Failed to fetch subscription", BillingErrorCodes.SERVICE_UNAVAILABLE);
        }
    }

    /// <summary>Get current workspace credits balance</summary>
    public async Task<Result<WorkspaceCreditsDto>> GetWorkspaceCreditsAsync(Guid workspaceId, CancellationToken ct = default)
    {
        if (workspaceId == Guid.Empty)
            return Result.Failure<WorkspaceCreditsDto>("Workspace ID cannot be empty", BillingErrorCodes.INVALID_WORKSPACE_ID);

        try
        {
            _logger.LogInformation("Fetching credits for workspace {WorkspaceId}", workspaceId);

            var activeSubscription = await _uow.Subscriptions
                .FirstOrDefaultAsync(
                    s => s.WorkspaceId == workspaceId && s.Status == SubscriptionStatus.Active,
                        "",
                        ct);

            if (activeSubscription is null)
            {
                _logger.LogInformation("No active subscription for workspace {WorkspaceId}", workspaceId);
                return Result.Failure<WorkspaceCreditsDto>(
                    "No active subscription found",
                    BillingErrorCodes.SUBSCRIPTION_NOT_FOUND);
            }

            var payload = new WorkspaceCreditsDto(
                activeSubscription.WorkspaceId,
                activeSubscription.CurrentCredits,
                activeSubscription.EndDate,
                activeSubscription.Status.ToString());

            _logger.LogInformation("Credits retrieved for workspace {WorkspaceId}: {Credits}", workspaceId, activeSubscription.CurrentCredits);
            return Result.Success(payload);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching credits for workspace {WorkspaceId}: {Message}", workspaceId, ex.Message);
            return Result.Failure<WorkspaceCreditsDto>($"Failed to fetch credits: {ex.Message}", BillingErrorCodes.SERVICE_UNAVAILABLE);
        }
    }

    /// <summary>Cancel workspace subscription</summary>
    public async Task<Result<WorkspaceCreditsDto>> CancelSubscriptionAsync(Guid workspaceId, string? reason = null, CancellationToken ct = default)
    {
        if (workspaceId == Guid.Empty)
            return Result.Failure<WorkspaceCreditsDto>("Workspace ID cannot be empty", BillingErrorCodes.INVALID_WORKSPACE_ID);

        try
        {
            _logger.LogInformation("Cancelling subscription for workspace {WorkspaceId}, reason: {Reason}", workspaceId, reason ?? "not provided");

            var subscription = await _uow.Subscriptions
                .FirstOrDefaultAsync(
                    s => s.WorkspaceId == workspaceId && s.Status == SubscriptionStatus.Active,
                        "",
                        ct);

            if (subscription is null)
            {
                _logger.LogWarning("No active subscription to cancel for workspace {WorkspaceId}", workspaceId);
                return Result.Failure<WorkspaceCreditsDto>("No active subscription found", BillingErrorCodes.SUBSCRIPTION_NOT_FOUND);
            }

            subscription.Status = SubscriptionStatus.Cancelled;
            subscription.EndDate = DateTime.UtcNow;

            _uow.Subscriptions.Update(subscription);
            await _uow.SaveChangesAsync(ct);

            _logger.LogInformation("Subscription cancelled for workspace {WorkspaceId}", workspaceId);
            return Result.Success(new WorkspaceCreditsDto(
                subscription.WorkspaceId,
                subscription.CurrentCredits,
                subscription.EndDate,
                subscription.Status.ToString()));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cancelling subscription for workspace {WorkspaceId}", workspaceId);
            return Result.Failure<WorkspaceCreditsDto>("Failed to cancel subscription", BillingErrorCodes.SERVICE_UNAVAILABLE);
        }
    }

    /// <summary>Top-up credits for workspace</summary>
    public async Task<Result<WorkspaceCreditsDto>> TopUpCreditsAsync(
        Guid workspaceId,
        int amount,
        string referenceType = "topup",
        Guid? referenceId = null,
        CancellationToken ct = default)
    {
        if (workspaceId == Guid.Empty)
            return Result.Failure<WorkspaceCreditsDto>("Workspace ID cannot be empty", BillingErrorCodes.INVALID_WORKSPACE_ID);
        if (amount <= 0)
            return Result.Failure<WorkspaceCreditsDto>("Amount must be greater than 0", BillingErrorCodes.INVALID_AMOUNT);

        try
        {
            _logger.LogInformation("Top-upping credits for workspace {WorkspaceId}: {Amount}", workspaceId, amount);

            var subscription = await _uow.Subscriptions
                .FirstOrDefaultAsync(
                    s => s.WorkspaceId == workspaceId && s.Status == SubscriptionStatus.Active,
                        "",
                        ct);

            if (subscription is null)
                return Result.Failure<WorkspaceCreditsDto>(
                    "No active subscription found",
                    BillingErrorCodes.SUBSCRIPTION_NOT_FOUND);

            subscription.CurrentCredits += amount;
            _uow.Subscriptions.Update(subscription);

            // Log credit transaction
            var creditTx = new CreditTransaction
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId,
                Amount = amount,
                Type = CreditTransactionType.TopUp,
                ReferenceId = referenceId,
                ReferenceType = referenceType != null ? Enum.Parse<CreditReferenceType>(referenceType, true) : null,
                CreatedAt = DateTime.UtcNow
            };

            await _uow.CreditTransactions.AddAsync(creditTx, ct);
            await _uow.SaveChangesAsync(ct);

            _logger.LogInformation("Credits topped up for workspace {WorkspaceId}: {Amount} (total: {NewTotal})",
                workspaceId, amount, subscription.CurrentCredits);

            return Result.Success(new WorkspaceCreditsDto(
                subscription.WorkspaceId,
                subscription.CurrentCredits,
                subscription.EndDate,
                subscription.Status.ToString()));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error topping up credits for workspace {WorkspaceId}", workspaceId);
            return Result.Failure<WorkspaceCreditsDto>($"Failed to top-up credits: {ex.Message}", BillingErrorCodes.CREDITS_OPERATION_FAILED);
        }
    }

    /// <summary>Consume credits for service usage</summary>
    public async Task<Result<WorkspaceCreditsDto>> ConsumeCreditsAsync(
        Guid workspaceId,
        int amount,
        string referenceType,
        Guid? referenceId = null,
        CancellationToken ct = default)
    {
        if (workspaceId == Guid.Empty)
            return Result.Failure<WorkspaceCreditsDto>("Workspace ID cannot be empty", BillingErrorCodes.INVALID_WORKSPACE_ID);
        if (amount <= 0)
            return Result.Failure<WorkspaceCreditsDto>("Amount must be greater than 0", BillingErrorCodes.INVALID_AMOUNT);
        if (string.IsNullOrWhiteSpace(referenceType))
            return Result.Failure<WorkspaceCreditsDto>("Reference type is required", BillingErrorCodes.VALIDATION_FAILED);

        try
        {
            _logger.LogInformation("Consuming {Amount} credits for workspace {WorkspaceId} (reference: {ReferenceType})",
                amount, workspaceId, referenceType);

            var subscription = await _uow.Subscriptions
                .FirstOrDefaultAsync(
                    s => s.WorkspaceId == workspaceId && s.Status == SubscriptionStatus.Active,
                        "",
                        ct);

            if (subscription is null)
                return Result.Failure<WorkspaceCreditsDto>(
                    "No active subscription found",
                    BillingErrorCodes.SUBSCRIPTION_NOT_FOUND);

            if (subscription.CurrentCredits < amount)
            {
                _logger.LogWarning("Insufficient credits for workspace {WorkspaceId}: has {Available}, needs {Required}",
                    workspaceId, subscription.CurrentCredits, amount);
                return Result.Failure<WorkspaceCreditsDto>(
                    $"Insufficient credits. Available: {subscription.CurrentCredits}, Required: {amount}",
                    BillingErrorCodes.INSUFFICIENT_CREDITS);
            }

            subscription.CurrentCredits -= amount;
            _uow.Subscriptions.Update(subscription);

            // Log credit transaction
            var creditTx = new CreditTransaction
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId,
                Amount = -amount,
                Type = CreditTransactionType.Consume,
                ReferenceId = referenceId,
                ReferenceType = Enum.Parse<CreditReferenceType>(referenceType, true),
                CreatedAt = DateTime.UtcNow
            };

            await _uow.CreditTransactions.AddAsync(creditTx, ct);
            await _uow.SaveChangesAsync(ct);

            _logger.LogInformation("Credits consumed for workspace {WorkspaceId}: -{Amount} (remaining: {NewBalance})",
                workspaceId, amount, subscription.CurrentCredits);

            return Result.Success(new WorkspaceCreditsDto(
                subscription.WorkspaceId,
                subscription.CurrentCredits,
                subscription.EndDate,
                subscription.Status.ToString()));
        }
        catch (DbUpdateConcurrencyException ex)
        {
            _logger.LogError(ex, "Concurrency conflict consuming credits for workspace {WorkspaceId}", workspaceId);
            return Result.Failure<WorkspaceCreditsDto>("Concurrent update detected", BillingErrorCodes.CONCURRENCY_CONFLICT);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error consuming credits for workspace {WorkspaceId}", workspaceId);
            return Result.Failure<WorkspaceCreditsDto>($"Failed to consume credits: {ex.Message}", BillingErrorCodes.CREDITS_OPERATION_FAILED);
        }
    }

    /// <summary>Get credit transaction history</summary>
    public async Task<Result<PaginatedResponse<CreditTransactionDto>>> GetCreditHistoryAsync(
        Guid workspaceId,
        int pageNumber = 1,
        int pageSize = 50,
        CancellationToken ct = default)
    {
        if (workspaceId == Guid.Empty)
            return Result.Failure<PaginatedResponse<CreditTransactionDto>>(
                "Workspace ID cannot be empty",
                BillingErrorCodes.INVALID_WORKSPACE_ID);
        if (pageNumber < 1 || pageSize < 1 || pageSize > 200)
            return Result.Failure<PaginatedResponse<CreditTransactionDto>>(
                "Invalid pagination parameters",
                BillingErrorCodes.VALIDATION_FAILED);

        try
        {
            _logger.LogInformation(
                "Fetching credit history for workspace {WorkspaceId}: page {PageNumber}, size {PageSize}",
                workspaceId, pageNumber, pageSize);

            var query = _uow.CreditTransactions
                .Query()
                .Where(ct => ct.WorkspaceId == workspaceId)
                .OrderByDescending(ct => ct.CreatedAt);

            var totalCount = await query.CountAsync(ct);
            var totalPages = (totalCount + pageSize - 1) / pageSize;

            var transactions = await query
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(ct);

            var items = transactions.Select(MapToDto).ToList();

            var response = new PaginatedResponse<CreditTransactionDto>(
                items,
                pageNumber,
                pageSize,
                totalCount,
                totalPages);

            _logger.LogInformation(
                "Retrieved {ItemCount} credit transactions for workspace {WorkspaceId}",
                items.Count, workspaceId);

            return Result.Success(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching credit history for workspace {WorkspaceId}", workspaceId);
            return Result.Failure<PaginatedResponse<CreditTransactionDto>>(
                "Failed to fetch credit history",
                BillingErrorCodes.SERVICE_UNAVAILABLE);
        }
    }

    /// <summary>Get payment transaction history</summary>
    public async Task<Result<PaginatedResponse<TransactionDto>>> GetTransactionHistoryAsync(
        Guid workspaceId,
        int pageNumber = 1,
        int pageSize = 50,
        CancellationToken ct = default)
    {
        if (workspaceId == Guid.Empty)
            return Result.Failure<PaginatedResponse<TransactionDto>>(
                "Workspace ID cannot be empty",
                BillingErrorCodes.INVALID_WORKSPACE_ID);
        if (pageNumber < 1 || pageSize < 1 || pageSize > 200)
            return Result.Failure<PaginatedResponse<TransactionDto>>(
                "Invalid pagination parameters",
                BillingErrorCodes.VALIDATION_FAILED);

        try
        {
            _logger.LogInformation(
                "Fetching transaction history for workspace {WorkspaceId}: page {PageNumber}, size {PageSize}",
                workspaceId, pageNumber, pageSize);

            var query = _uow.Transactions
                .Query()
                .Where(t => t.WorkspaceId == workspaceId)
                .OrderByDescending(t => t.CreatedAt);

            var totalCount = await query.CountAsync(ct);
            var totalPages = (totalCount + pageSize - 1) / pageSize;

            var transactions = await query
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(ct);

            var items = transactions.Select(MapToDto).ToList();

            var response = new PaginatedResponse<TransactionDto>(
                items,
                pageNumber,
                pageSize,
                totalCount,
                totalPages);

            _logger.LogInformation(
                "Retrieved {ItemCount} payment transactions for workspace {WorkspaceId}",
                items.Count, workspaceId);

            return Result.Success(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching transaction history for workspace {WorkspaceId}", workspaceId);
            return Result.Failure<PaginatedResponse<TransactionDto>>(
                "Failed to fetch transaction history",
                BillingErrorCodes.SERVICE_UNAVAILABLE);
        }
    }

    // ===== PRIVATE HELPERS =====

    private SubscriptionDto MapToDto(Subscription subscription)
    {
        return new SubscriptionDto(
            subscription.Id,
            subscription.WorkspaceId,
            subscription.PlanId,
            subscription.Status.ToString(),
            subscription.CurrentCredits,
            subscription.StartDate,
            subscription.EndDate,
            subscription.CreatedAt);
    }

    private CreditTransactionDto MapToDto(CreditTransaction tx)
    {
        return new CreditTransactionDto(
            tx.Id,
            tx.WorkspaceId,
            tx.Amount,
            tx.Type.ToString(),
            tx.ReferenceId,
            tx.ReferenceType?.ToString(),
            tx.CreatedAt);
    }

    private TransactionDto MapToDto(Transaction tx)
    {
        return new TransactionDto(
            tx.Id,
            tx.WorkspaceId,
            tx.SubscriptionId,
            tx.Amount,
            tx.Status.ToString(),
            tx.ExternalId,
            tx.CreatedAt);
    }
}
