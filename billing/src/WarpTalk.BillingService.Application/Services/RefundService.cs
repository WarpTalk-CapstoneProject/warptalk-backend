using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Services;

public class RefundService : IRefundService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<RefundService> _logger;

    public RefundService(IUnitOfWork unitOfWork, ILogger<RefundService> logger)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task<Result<RefundDto>> RefundPaymentAsync(
        Guid paymentId, decimal amount, string reason, CancellationToken cancellationToken = default)
    {
        try
        {
            var payment = await _unitOfWork.PaymentRepository.GetByIdAsync(paymentId, cancellationToken);
            if (payment == null)
                return Result.Failure<RefundDto>("Payment transaction not found.", "NOT_FOUND");

            if (payment.Status != "paid")
                return Result.Failure<RefundDto>("Only paid transactions can be refunded.", "INVALID_REQUEST");

            if (amount <= 0 || amount > payment.Amount)
                return Result.Failure<RefundDto>("Refund amount must be positive and cannot exceed original payment amount.", "INVALID_REQUEST");

            var refund = new Refund
            {
                Id = Guid.NewGuid(),
                PaymentId = paymentId,
                UserId = payment.UserId,
                Amount = amount,
                Reason = reason,
                Status = "completed",
                CreatedAt = DateTime.UtcNow,
                CompletedAt = DateTime.UtcNow
            };

            await _unitOfWork.RefundRepository.AddAsync(refund, cancellationToken);
            
            // Mark payment as refunded (fully or partially)
            payment.RefundedAt = DateTime.UtcNow;
            _unitOfWork.PaymentRepository.Update(payment);

            await _unitOfWork.SaveChangesAsync(cancellationToken);

            var dto = new RefundDto
            {
                Id = refund.Id.ToString(),
                PaymentId = refund.PaymentId.ToString(),
                Amount = refund.Amount,
                Reason = refund.Reason ?? string.Empty,
                Status = refund.Status,
                CreatedAt = refund.CreatedAt,
                CompletedAt = refund.CompletedAt
            };

            return Result.Success(dto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing refund for PaymentId {PaymentId}", paymentId);
            return Result.Failure<RefundDto>("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }
}
