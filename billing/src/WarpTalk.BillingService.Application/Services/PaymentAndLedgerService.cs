using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Mappers;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Services;

public class PaymentAndLedgerService : IPaymentAndLedgerService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<PaymentAndLedgerService> _logger;

    public PaymentAndLedgerService(IUnitOfWork unitOfWork, ILogger<PaymentAndLedgerService> logger)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    // --- Ledger Methods ---

    public async Task<int> CalculateBalanceAsync(Guid subscriptionId, CancellationToken cancellationToken = default)
    {
        // Get the latest snapshot
        var snapshots = await _unitOfWork.CreditBalanceSnapshotRepository.GetPagedAsync(
            predicate: s => s.SubscriptionId == subscriptionId,
            skip: 0,
            take: 1,
            orderBy: q => q.OrderByDescending(s => s.SnapshotAt),
            cancellationToken: cancellationToken);

        var snapshot = snapshots.FirstOrDefault();

        var baseBalance = snapshot?.CreditsRemaining ?? 0;
        var fromDate = snapshot?.SnapshotAt ?? DateTime.MinValue;

        // Get all relevant ledger entries since the snapshot
        var entries = await _unitOfWork.CreditTransactionRepository
            .FindAsync(tx => tx.SubscriptionId == subscriptionId && tx.CreatedAt >= fromDate, cancellationToken);

        var netChange = 0;

        foreach (var entry in entries)
        {
            switch (entry.Type.ToLower())
            {
                case "top_up":
                case "refund":
                case "adjustment":
                    netChange += entry.Amount;
                    break;

                case "consume":
                    netChange -= entry.Amount;
                    break;
            }
        }

        return baseBalance + netChange;
    }

    // --- Payment Methods ---

    public async Task<Result<PagedResult<PaymentTransactionDto>>> GetPaymentHistoryAsync(
        Guid workspaceId, int pageNumber, int pageSize, CancellationToken cancellationToken = default)
    {
        try
        {
            var sub = await _unitOfWork.SubscriptionRepository.FirstOrDefaultAsync(
                s => s.WorkspaceId == workspaceId && s.IsActive && s.DeletedAt == null,
                cancellationToken);

            if (sub is null)
                return Result.Failure<PagedResult<PaymentTransactionDto>>(
                    "No active subscription found for this workspace.",
                    ErrorCodes.BillingSubscriptionNotFound);

            var size = pageSize > 0 ? pageSize : 20;
            var skip = ((pageNumber > 0 ? pageNumber : 1) - 1) * size;

            var items = await _unitOfWork.PaymentRepository.GetPagedAsync(
                p => p.SubscriptionId == sub.Id,
                skip, size,
                q => q.OrderByDescending(p => p.CreatedAt),
                cancellationToken);

            var total = await _unitOfWork.PaymentRepository.CountAsync(
                p => p.SubscriptionId == sub.Id,
                cancellationToken);

            return Result.Success(new PagedResult<PaymentTransactionDto>(
                total,
                items.Select(p => p.ToDto()).ToList()));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting payment history for WorkspaceId {WorkspaceId}", workspaceId);
            return Result.Failure<PagedResult<PaymentTransactionDto>>("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }


    public async Task<Result<PaymentTransactionDto>> CreatePaymentAsync(
        CreatePaymentRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var sub = await _unitOfWork.SubscriptionRepository.GetByIdAsync(request.SubscriptionId, cancellationToken);
            if (sub == null) return Result.Failure<PaymentTransactionDto>("Subscription not found.", "NOT_FOUND");

            var plan = await _unitOfWork.PlanRepository.GetByIdAsync(sub.PlanId, cancellationToken);
            if (plan == null) return Result.Failure<PaymentTransactionDto>("Plan not found.", "NOT_FOUND");

            decimal finalAmount = plan.Price;
            if (plan.BillingCycle.Equals("semiannual", StringComparison.OrdinalIgnoreCase))
            {
                finalAmount *= 0.9m; // 10% discount
            }
            else if (plan.BillingCycle.Equals("yearly", StringComparison.OrdinalIgnoreCase))
            {
                finalAmount *= 0.8m; // 20% discount
            }

            if (finalAmount <= 0)
                return Result.Failure<PaymentTransactionDto>("Payment amount must be greater than zero.", "INVALID_REQUEST");

            var payment = request.ToEntity(finalAmount, plan.Currency);

            await _unitOfWork.PaymentRepository.AddAsync(payment, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            return Result.Success(payment.ToDto());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating payment for SubscriptionId {SubscriptionId}", request.SubscriptionId);
            return Result.Failure<PaymentTransactionDto>("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result<PaymentTransactionDto>> UpdatePaymentStatusAsync(
        Guid paymentId, string status, string? providerTransactionId, string? failureReason,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var payment = await _unitOfWork.PaymentRepository.GetByIdAsync(paymentId, cancellationToken);
            if (payment is null)
                return Result.Failure<PaymentTransactionDto>(
                    $"Payment '{paymentId}' not found.",
                    ErrorCodes.NotFound);

            payment.Status = status;
            payment.ProviderTransactionId = providerTransactionId ?? payment.ProviderTransactionId;
            payment.FailureReason = failureReason;
            payment.UpdatedAt = DateTime.UtcNow;

            if (status == "paid")
                payment.PaidAt = DateTime.UtcNow;

            _unitOfWork.PaymentRepository.Update(payment);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            return Result.Success(payment.ToDto());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating payment status for PaymentId {PaymentId}", paymentId);
            return Result.Failure<PaymentTransactionDto>("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result<bool>> HandleWebhookAsync(
        PaymentWebhookRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!Guid.TryParse(request.OrderCode, out var paymentId))
                return Result.Failure<bool>("Invalid OrderCode.", "INVALID_REQUEST");

            var payment = await _unitOfWork.PaymentRepository.GetByIdAsync(paymentId, cancellationToken);
            if (payment == null)
                return Result.Failure<bool>("Payment not found.", "NOT_FOUND");

            // Idempotent Check — already processed
            if (payment.Status == "paid")
                return Result.Success(true);

            if (request.Status.Equals("PAID", StringComparison.OrdinalIgnoreCase))
            {
                var sub = await _unitOfWork.SubscriptionRepository.GetByIdAsync(payment.SubscriptionId, cancellationToken);
                if (sub == null)
                {
                    _logger.LogError("HandleWebhookAsync: Subscription {SubscriptionId} not found for paid payment {PaymentId}. Aborting activation.", payment.SubscriptionId, paymentId);
                    return Result.Failure<bool>("Subscription not found for this payment.", ErrorCodes.BillingSubscriptionNotFound);
                }

                var plan = await _unitOfWork.PlanRepository.GetByIdAsync(sub.PlanId, cancellationToken);
                if (plan == null)
                {
                    _logger.LogError("HandleWebhookAsync: Plan {PlanId} not found for subscription {SubscriptionId}. Aborting activation.", sub.PlanId, sub.Id);
                    return Result.Failure<bool>("Plan not found for this subscription.", ErrorCodes.BillingPlanNotFound);
                }

                if (sub.Status != "pending")
                    return Result.Failure<bool>("Auto-renew is not supported at this time.", "NOT_SUPPORTED");

                // Mark payment paid
                payment.Status = "paid";
                payment.PaidAt = DateTime.UtcNow;
                payment.ProviderTransactionId = request.TransactionId;
                _unitOfWork.PaymentRepository.Update(payment);

                // Deactivate any other active subscriptions for this user
                var existingSubs = await _unitOfWork.SubscriptionRepository.GetPagedAsync(
                    s => s.UserId == sub.UserId && s.IsActive && s.Id != sub.Id,
                    0, 10, null, cancellationToken);
                foreach (var oldSub in existingSubs)
                {
                    oldSub.AutoRenew = false;
                    oldSub.Status = "cancelled";
                    oldSub.UpdatedAt = DateTime.UtcNow;
                    _unitOfWork.SubscriptionRepository.Update(oldSub);
                }

                // Activate pending subscription
                sub.Status = "active";
                sub.IsActive = true;
                sub.CurrentPeriodStart = DateTime.UtcNow;
                sub.CurrentPeriodEnd = plan.BillingCycle switch
                {
                    "yearly" => DateTime.UtcNow.AddYears(1),
                    "semiannual" => DateTime.UtcNow.AddMonths(6),
                    _ => DateTime.UtcNow.AddMonths(1)
                };
                sub.CreditsRemaining += plan.CreditsPerCycle;
                sub.CreditsUsedThisCycle = 0;
                sub.UpdatedAt = DateTime.UtcNow;
                _unitOfWork.SubscriptionRepository.Update(sub);

                var topupTx = new WarpTalk.BillingService.Domain.Entities.CreditTransaction
                {
                    SubscriptionId = sub.Id,
                    UserId = sub.UserId,
                    Amount = plan.CreditsPerCycle,
                    Type = "top_up",
                    Description = "Subscription activation top-up",
                    ReferenceId = payment.Id,
                    ReferenceType = "payment",
                    BalanceAfter = sub.CreditsRemaining,
                    CreatedAt = DateTime.UtcNow
                };
                await _unitOfWork.CreditTransactionRepository.AddAsync(topupTx, cancellationToken);
            }
            else
            {
                payment.Status = "failed";
                payment.FailureReason = request.Status;
                payment.UpdatedAt = DateTime.UtcNow;
                _unitOfWork.PaymentRepository.Update(payment);
            }

            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return Result.Success(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling webhook for OrderCode {OrderCode}", request.OrderCode);
            return Result.Failure<bool>("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }
}
