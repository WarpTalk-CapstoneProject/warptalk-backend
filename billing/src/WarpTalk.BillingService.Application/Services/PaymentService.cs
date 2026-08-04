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
    private readonly IBillingPolicyService _billingPolicyService;

    public PaymentService(
        IUnitOfWork unitOfWork,
        ILogger<PaymentService> logger,
        IBillingPolicyService billingPolicyService)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
        _billingPolicyService = billingPolicyService;
    }

    // --- Ledger Methods ---



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

            var page = await _unitOfWork.PaymentRepository.GetHistoryPageAsync(
                sub.Id,
                BillingQueryHelper.ToPageRequest(query),
                cancellationToken);

            return Result.Success(PaginatedResponse<PaymentTransactionDto>.Create(
                page.Items.Select(p => p.ToDto()).ToList(), page.TotalCount, page.PageNumber, page.PageSize));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, BillingMessageConstants.LogMessages.ErrorGettingPaymentHistory, workspaceId);
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

            var plan = await _unitOfWork.Plans.GetByIdAsync(sub.PlanId, cancellationToken);
            if (plan == null) return Result.Failure<PaymentTransactionDto>(ApiMessageConstants.ErrorMessages.BillingPlanNotFound, ErrorCodes.NotFound);
            decimal finalAmount = plan.Price;
            var billingPolicy = await _billingPolicyService.GetPolicyAsync(cancellationToken);

            if (finalAmount <= 0)
                return Result.Failure<PaymentTransactionDto>(ApiMessageConstants.ErrorMessages.BillingInvalidAmount, ErrorCodes.BillingInvalidAmount);

            var taxAmount = Math.Round(
                finalAmount * billingPolicy.VatRate,
                2,
                MidpointRounding.AwayFromZero);
            var payment = request.ToEntity(finalAmount, plan.Currency, taxAmount);

            await _unitOfWork.PaymentRepository.AddAsync(payment, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            return Result.Success(payment.ToDto());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, BillingMessageConstants.LogMessages.ErrorCreatingPayment, request.SubscriptionId);
            return Result.Failure<PaymentTransactionDto>(ApiMessageConstants.ErrorMessages.BillingInternalError, ErrorCodes.InternalServerError);
        }
    }


}
