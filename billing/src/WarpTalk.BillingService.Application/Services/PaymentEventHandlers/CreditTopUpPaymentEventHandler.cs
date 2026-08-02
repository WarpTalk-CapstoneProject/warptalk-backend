using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Mappers;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Services.PaymentEventHandlers;

public sealed class CreditTopUpPaymentEventHandler : IPaymentEventHandler
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICreditGrantService _creditGrantService;
    private readonly ILogger<CreditTopUpPaymentEventHandler> _logger;

    public CreditTopUpPaymentEventHandler(
        IUnitOfWork unitOfWork,
        ICreditGrantService creditGrantService,
        ILogger<CreditTopUpPaymentEventHandler> logger)
    {
        _unitOfWork = unitOfWork;
        _creditGrantService = creditGrantService;
        _logger = logger;
    }

    public bool CanHandle(PaymentEventContext context)
        => context.Request.PaymentType == PaymentConstants.PaymentTypes.CreditTopUp;

    public async Task<Result> HandleAsync(PaymentEventContext context, CancellationToken cancellationToken = default)
    {
        if (context.Subscription is null)
        {
            _logger.LogError(BillingMessageConstants.LogMessages.NoActiveSubscriptionForTopUp, context.WorkspaceId);
            return Result.Failure(BillingMessageConstants.ApiErrorMessages.BillingTopUpSubscriptionNotFound, ErrorCodes.BillingSubscriptionNotFound);
        }

        if (context.ParsedPaymentStatus != PaymentConstants.PaymentStatuses.Paid)
        {
            return Result.Success();
        }

        var creditsAdded = string.Equals(context.Request.Currency, PaymentConstants.Currencies.Vnd, StringComparison.OrdinalIgnoreCase)
            ? (int)context.Request.Amount
            : (int)(context.Request.Amount * 100m);

        var grantResult = await _creditGrantService.QueueCreditGrantAsync(
            context.Subscription,
            new GrantCreditsRequest(
                context.WorkspaceId,
                creditsAdded,
                TransactionConstants.ReferenceTypes.StripePayment,
                context.PaymentId,
                context.UserId,
                BillingMessageConstants.SuccessMessages.StripeCreditTopUp),
            cancellationToken);

        if (!grantResult.IsSuccess)
        {
            return Result.Failure(grantResult.Error ?? ApiMessageConstants.ErrorMessages.BillingInternalError, grantResult.ErrorCode);
        }

        if (context.ExistingPayment is null)
        {
            var payment = CreatePayment(context, context.Subscription);
            await _unitOfWork.PaymentRepository.AddAsync(payment, cancellationToken);
            context.ExistingPayment = payment;
        }

        return Result.Success();
    }

    private static Payment CreatePayment(PaymentEventContext context, Subscription subscription)
    {
        var payment = PaymentMapper.CreateStripePayment(new StripePaymentCreationRequest(
            SubscriptionId: subscription.Id,
            UserId: context.UserId,
            Amount: context.Request.Amount,
            Currency: context.Request.Currency,
            ProviderTransactionId: context.ProviderTransactionId,
            Status: context.ParsedPaymentStatus,
            FailureReason: context.Request.FailureReason));
        payment.Id = context.PaymentId;
        return payment;
    }
}
