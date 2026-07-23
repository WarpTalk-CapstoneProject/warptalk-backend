using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Mappers;
using WarpTalk.BillingService.Application.Helpers;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;

using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Services;

public class PaymentService : IPaymentService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<PaymentService> _logger;

    public PaymentService(IUnitOfWork unitOfWork, ILogger<PaymentService> logger)
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
            string type = entry.Type.ToLower();
            if (type == BillingConstants.TransactionTypes.TopUp || 
                type == BillingConstants.TransactionTypes.Refund || 
                type == BillingConstants.TransactionTypes.Adjustment)
            {
                netChange += entry.Amount;
            }
            else if (type == BillingConstants.TransactionTypes.Consume)
            {
                netChange -= entry.Amount;
            }
        }

        return baseBalance + netChange;
    }

    public async Task<Result<PaginatedResponse<PaymentTransactionDto>>> GetPaymentHistoryAsync(
        Guid workspaceId, PaginationQuery query, CancellationToken cancellationToken = default)
    {
        try
        {   //find latest active subscription for workspace
            var sub = await _unitOfWork.SubscriptionRepository.FirstOrDefaultAsync(
                s => s.WorkspaceId == workspaceId && s.IsActive && s.DeletedAt == null,
                cancellationToken);

            if (sub is null)
                return Result.Failure<PaginatedResponse<PaymentTransactionDto>>(
                    ApiMessageConstants.ErrorMessages.BillingSubscriptionNotFound,
                    ErrorCodes.BillingSubscriptionNotFound);

            var size = Math.Clamp(query.PageSize, 1, 200);
            var skip = (Math.Max(1, query.PageNumber) - 1) * size;
            //get payment history for workspace
            var items = await _unitOfWork.PaymentRepository.GetPagedAsync(
                p => p.SubscriptionId == sub.Id,
                skip, size,
                q => q.OrderByDescending(p => p.CreatedAt),
                cancellationToken);
            //get total payment history for workspace
            var total = await _unitOfWork.PaymentRepository.CountAsync(
                p => p.SubscriptionId == sub.Id,
                cancellationToken);

            return Result.Success(PaginatedResponse<PaymentTransactionDto>.Create(
                items.Select(p => p.ToDto()).ToList(), total, Math.Max(1, query.PageNumber), size));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, BillingConstants.LogMessages.ErrorGettingPaymentHistory, workspaceId);
            return Result.Failure<PaginatedResponse<PaymentTransactionDto>>(ApiMessageConstants.ErrorMessages.BillingInternalError, ErrorCodes.InternalServerError);
        }
    }


    public async Task<Result<PaymentTransactionDto>> CreatePaymentAsync(
        CreatePaymentRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var sub = await _unitOfWork.SubscriptionRepository.GetByIdAsync(request.SubscriptionId, cancellationToken);
            if (sub == null) return Result.Failure<PaymentTransactionDto>(ApiMessageConstants.ErrorMessages.BillingSubscriptionNotFound, ErrorCodes.NotFound);

            var plan = await _unitOfWork.PlanRepository.GetByIdAsync(sub.PlanId, cancellationToken);
            if (plan == null) return Result.Failure<PaymentTransactionDto>(ApiMessageConstants.ErrorMessages.BillingPlanNotFound, ErrorCodes.NotFound);
            //get final amount (discount)
            decimal finalAmount = plan.Price;
            if (plan.BillingCycle.Equals(BillingConstants.BillingCycles.Semiannual, StringComparison.OrdinalIgnoreCase))
            {
                finalAmount *= 0.9m; // 10% discount
            }
            else if (plan.BillingCycle.Equals(BillingConstants.BillingCycles.Yearly, StringComparison.OrdinalIgnoreCase))
            {
                finalAmount *= 0.8m; // 20% discount
            }

            if (finalAmount <= 0)
                return Result.Failure<PaymentTransactionDto>(ApiMessageConstants.ErrorMessages.BillingInvalidAmount, ErrorCodes.BillingInvalidAmount);

            var payment = request.ToEntity(finalAmount, plan.Currency);

            await _unitOfWork.PaymentRepository.AddAsync(payment, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            return Result.Success(payment.ToDto());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, BillingConstants.LogMessages.ErrorCreatingPayment, request.SubscriptionId);
            return Result.Failure<PaymentTransactionDto>(ApiMessageConstants.ErrorMessages.BillingInternalError, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<PaymentTransactionDto>> UpdatePaymentStatusAsync(
        Guid paymentId, UpdatePaymentStatusRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var payment = await _unitOfWork.PaymentRepository.GetByIdAsync(paymentId, cancellationToken);
            if (payment is null)
                return Result.Failure<PaymentTransactionDto>(
                    ApiMessageConstants.ErrorMessages.BillingPaymentNotFound,
                    ErrorCodes.NotFound);

            if (request.Status.Equals("paid", StringComparison.OrdinalIgnoreCase))
            {
                payment.Status = BillingConstants.PaymentStatuses.Paid;
            }
            else
            {
                payment.Status = request.Status.ToLower();
            }
            payment.ProviderTransactionId = request.ProviderTransactionId ?? payment.ProviderTransactionId;
            payment.FailureReason = request.FailureReason;
            payment.UpdatedAt = DateTime.UtcNow;

            if (payment.Status == BillingConstants.PaymentStatuses.Paid)
                payment.PaidAt = DateTime.UtcNow;

            _unitOfWork.PaymentRepository.Update(payment);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            return Result.Success(payment.ToDto());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, BillingConstants.LogMessages.ErrorUpdatingPaymentStatus, paymentId);
            return Result.Failure<PaymentTransactionDto>(ApiMessageConstants.ErrorMessages.BillingInternalError, ErrorCodes.InternalServerError);
        }
    }

    // Webhook receiver to handle payOS/stripe checkout completion asynchronously
    public async Task<Result<bool>> HandleWebhookAsync(
        PaymentWebhookRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // 1. Parse and validate the payment ID from order code string
            if (!Guid.TryParse(request.OrderCode, out Guid paymentId))
            {
                // Return validation failure response if GUID parsing fails
                return Result.Failure<bool>(ApiMessageConstants.ErrorMessages.BillingInvalidOrderCode, ErrorCodes.ValidationError);
            }

            // 2. Fetch the payment record and eagerly include its linked Subscription details from DB using repository method
            Payment? payment = await _unitOfWork.PaymentRepository.GetWithSubscriptionAsync(paymentId, cancellationToken);

            // Check if payment entity exists in database
            if (payment == null)
            {
                // Return payment not found error response
                return Result.Failure<bool>(ApiMessageConstants.ErrorMessages.BillingPaymentNotFound, ErrorCodes.NotFound);
            }

            // Check if subscription navigation entity is populated
            Subscription? sub = payment.Subscription;
            if (sub == null)
            {
                // Return subscription not found error response
                return Result.Failure<bool>(ApiMessageConstants.ErrorMessages.BillingSubscriptionNotFound, ErrorCodes.BillingSubscriptionNotFound);
            }

            // Extract the workspace ID to serve as lock key for concurrency retry
            Guid workspaceId = sub.WorkspaceId;

            // 3. Execute the transactional DB update block using Concurrency Retry Lock
            return await ConcurrencyRetryHelper.ExecuteWithConcurrencyRetryAsync(_unitOfWork, _logger, workspaceId, async () =>
            {
                // Eagerly reload the payment, subscription and plan entities within lock to get freshest DB state using repository method
                Payment? currentPayment = await _unitOfWork.PaymentRepository.GetWithSubscriptionAndPlanAsync(paymentId, cancellationToken);

                // Re-verify payment exists in database inside transaction block
                if (currentPayment == null)
                {
                    // Return payment not found error response
                    return Result.Failure<bool>(ApiMessageConstants.ErrorMessages.BillingPaymentNotFound, ErrorCodes.NotFound);
                }

                // Idempotent Check — skip processing if webhook already processed payment as PAID
                if (currentPayment.Status == BillingConstants.PaymentStatuses.Paid)
                {
                    // Return success true immediately
                    return Result.Success(true);
                }

                // 4. Handle checkout success flow (Status is Paid)
                if (request.Status.Equals(BillingConstants.PaymentStatuses.Paid, StringComparison.OrdinalIgnoreCase))
                {
                    // Extract subscription from refreshed payment entity
                    Subscription? currentSub = currentPayment.Subscription;
                    if (currentSub == null)
                    {
                        // Log critical error if subscription is missing
                        _logger.LogError(BillingConstants.LogMessages.WebhookSubscriptionNotFound, currentPayment.SubscriptionId, paymentId);
                        // Return subscription not found error response
                        return Result.Failure<bool>(ApiMessageConstants.ErrorMessages.BillingSubscriptionNotFound, ErrorCodes.BillingSubscriptionNotFound);
                    }

                    // Extract plan from refreshed subscription entity
                    Plan? plan = currentSub.Plan;
                    if (plan == null)
                    {
                        // Log critical error if plan is missing
                        _logger.LogError(BillingConstants.LogMessages.WebhookPlanNotFound, currentSub.PlanId, currentSub.Id);
                        // Return plan not found error response
                        return Result.Failure<bool>(ApiMessageConstants.ErrorMessages.BillingPlanNotFound, ErrorCodes.BillingPlanNotFound);
                    }

                    // Ensure the subscription is pending activation (e.g. newly created for checkout)
                    if (currentSub.Status != BillingConstants.SubscriptionStatuses.Pending)
                    {
                        // Return state conflict error if subscription is not pending
                        return Result.Failure<bool>(ApiMessageConstants.ErrorMessages.BillingAutoRenewNotSupported, ErrorCodes.InvalidState);
                    }

                    // Update payment record to PAID status
                    currentPayment.Status = BillingConstants.PaymentStatuses.Paid;
                    // Log current time as payment date
                    currentPayment.PaidAt = DateTime.UtcNow;
                    // Associate Stripe/PayOS external transaction ID
                    currentPayment.ProviderTransactionId = request.TransactionId;

                    // Deactivate any other active subscriptions for this user to avoid conflicts using optimized bulk update repo method
                    await _unitOfWork.SubscriptionRepository.DeactivateOtherActiveSubscriptionsAsync(currentSub.UserId, currentSub.Id, cancellationToken);

                    // Activate the new pending subscription and calculate period end date using switch expression
                    currentSub.Status = BillingConstants.SubscriptionStatuses.Active; // Set active status
                    currentSub.IsActive = true; // Mark subscription active
                    currentSub.CurrentPeriodStart = DateTime.UtcNow; // Log cycle start time
                    currentSub.CurrentPeriodEnd = plan.BillingCycle.ToLower() switch // Calculate end time
                    {
                        BillingConstants.BillingCycles.Yearly => DateTime.UtcNow.AddYears(1), // Add 1 year for yearly cycle
                        BillingConstants.BillingCycles.Semiannual => DateTime.UtcNow.AddMonths(6), // Add 6 months for semiannual cycle
                        _ => DateTime.UtcNow.AddMonths(1) // Default to 1 month for other cycles
                    };
                    
                    // Top up subscription with plan's cycle credits and reset tracker
                    currentSub.CreditsRemaining += plan.CreditsPerCycle; // Credit allocation
                    currentSub.CreditsUsedThisCycle = 0; // Reset usage counter
                    currentSub.UpdatedAt = DateTime.UtcNow; // Update timestamp

                    // Create credit ledger entry to track activation event
                    CreditTransaction topupTx = new CreditTransaction
                    {
                        SubscriptionId = currentSub.Id, // Connect to subscription
                        UserId = currentSub.UserId, // Connect to user
                        Amount = plan.CreditsPerCycle, // Allocation amount
                        Type = BillingConstants.TransactionTypes.TopUp, // Log transaction type
                        Description = BillingConstants.SuccessMessages.SubscriptionActivationTopUp, // Ledger description
                        ReferenceId = currentPayment.Id, // Link to payment ID
                        ReferenceType = BillingConstants.ReferenceTypes.Payment, // Reference type
                        BalanceAfter = currentSub.CreditsRemaining, // Post-transaction balance
                        CreatedAt = DateTime.UtcNow // Creation timestamp
                    };
                    await _unitOfWork.CreditTransactionRepository.AddAsync(topupTx, cancellationToken); // Queue save transaction
                }
                // 5. Handle checkout failure flow (Status is not Paid)
                else
                {
                    // Update payment record to FAILED status
                    currentPayment.Status = BillingConstants.PaymentStatuses.Failed;
                    // Record reason text returned from checkout webhook
                    currentPayment.FailureReason = request.Status;
                    // Log update timestamp
                    currentPayment.UpdatedAt = DateTime.UtcNow;
                }

                // Save all pending repository modifications atomically inside lock
                await _unitOfWork.SaveChangesAsync(cancellationToken);
                // Return success boolean result
                return Result.Success(true);
            }, cancellationToken);
        }
        // 6. Global Catch exception logging block
        catch (Exception ex)
        {
            // Log full exception stack trace with OrderCode context
            _logger.LogError(ex, BillingConstants.LogMessages.ErrorHandlingWebhook, request.OrderCode);
            // Return internal server error response
            return Result.Failure<bool>(ApiMessageConstants.ErrorMessages.BillingInternalError, ErrorCodes.InternalServerError);
        }
    }
}
