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
        Guid subscriptionId, Guid userId, decimal amount, decimal taxAmount,
        string currency, string paymentMethod, string provider,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var now = DateTime.UtcNow;
            var payment = new Payment
            {
                Id = Guid.NewGuid(),
                SubscriptionId = subscriptionId,
                UserId = userId,
                Amount = amount,
                TaxAmount = taxAmount,
                TotalAmount = amount + taxAmount,
                Currency = currency,
                PaymentMethod = paymentMethod,
                Provider = provider,
                Status = "pending",
                CreatedAt = now,
                UpdatedAt = now
            };

            await _unitOfWork.PaymentRepository.AddAsync(payment, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            return Result.Success(payment.ToDto());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating payment for SubscriptionId {SubscriptionId}", subscriptionId);
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
}
