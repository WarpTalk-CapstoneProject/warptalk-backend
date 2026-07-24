using WarpTalk.BillingService.Domain.Constants;
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Mappers;
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
        Guid paymentId, RefundPaymentRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var payment = await _unitOfWork.PaymentRepository.GetByIdAsync(paymentId, cancellationToken);
            if (payment == null)
                return Result.Failure<RefundDto>("Payment transaction not found.", ErrorCodes.NotFound);

            if (payment.Status != PaymentConstants.PaymentStatuses.Paid)
                return Result.Failure<RefundDto>("Only paid transactions can be refunded.", ErrorCodes.ValidationError);

            if (request.Amount <= 0 || request.Amount > payment.Amount)
                return Result.Failure<RefundDto>("Refund amount must be positive and cannot exceed original payment amount.", ErrorCodes.ValidationError);

            var refund = RefundMapper.CreateRefund(
                paymentId: payment.Id,
                amount: request.Amount,
                reason: request.Reason,
                status: TransactionConstants.RefundStatuses.Succeeded
            );

            await _unitOfWork.RefundRepository.AddAsync(refund, cancellationToken);

            payment.RefundedAt = DateTime.UtcNow;
            _unitOfWork.PaymentRepository.Update(payment);

            await _unitOfWork.SaveChangesAsync(cancellationToken);

            return Result.Success(refund.ToDto());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing refund for PaymentId {PaymentId}", paymentId);
            return Result.Failure<RefundDto>("An unexpected error occurred.", ErrorCodes.InternalServerError);
        }
    }
}
