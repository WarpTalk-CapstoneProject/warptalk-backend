using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Mappers;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
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
                items.Select(p => p.ToDto())));
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
            {
                return Result.Failure<bool>("Payment not found.", "NOT_FOUND");
            }

            // Idempotent Check
            if (payment.Status == "paid")
            {
                return Result.Success(true); // Already processed
            }

            if (request.Status.Equals("PAID", StringComparison.OrdinalIgnoreCase))
            {
                payment.Status = "paid";
                payment.PaidAt = DateTime.UtcNow;
                payment.ProviderTransactionId = request.TransactionId;
                _unitOfWork.PaymentRepository.Update(payment);

                var sub = await _unitOfWork.SubscriptionRepository.GetByIdAsync(payment.SubscriptionId, cancellationToken);
                if (sub != null)
                {
                    var plan = await _unitOfWork.PlanRepository.GetByIdAsync(sub.PlanId, cancellationToken);
                    if (plan != null)
                    {
                        if (sub.Status == "pending")
                        {
                            // Deactivate any existing active subscriptions for this user
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

                            sub.Status = "active";
                            sub.IsActive = true;
                            sub.CurrentPeriodStart = DateTime.UtcNow;
                            sub.CurrentPeriodEnd = plan.BillingCycle switch
                            {
                                "yearly" => DateTime.UtcNow.AddYears(1),
                                "semiannual" => DateTime.UtcNow.AddMonths(6),
                                _ => DateTime.UtcNow.AddMonths(1)
                            };
                            sub.CreditsRemaining += plan.CreditsPerCycle; // += to keep carry-over
                            sub.CreditsUsedThisCycle = 0;
                            sub.UpdatedAt = DateTime.UtcNow;
                            _unitOfWork.SubscriptionRepository.Update(sub);
                        }
                        else
                        {
                            return Result.Failure<bool>("Auto-renew is not supported at this time.", "NOT_SUPPORTED");
                        }
                    }
                }
            }
            else
            {
                payment.Status = "failed";
                payment.FailureReason = request.Status;
                payment.UpdatedAt = DateTime.UtcNow;
                _unitOfWork.PaymentRepository.Update(payment);
            }

            return Result.Success(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling webhook for OrderCode {OrderCode}", request.OrderCode);
            return Result.Failure<bool>("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }
}
