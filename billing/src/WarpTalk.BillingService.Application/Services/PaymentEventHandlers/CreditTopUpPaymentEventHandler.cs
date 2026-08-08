using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Helpers;
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
    private readonly ILogger<CreditTopUpPaymentEventHandler> _logger;

    public CreditTopUpPaymentEventHandler(
        IUnitOfWork unitOfWork,
        ILogger<CreditTopUpPaymentEventHandler> logger)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public bool CanHandle(PaymentEventContext context)
        => context.Request.PaymentType == PaymentConstants.PaymentTypes.CreditTopUp;

    public async Task<Result> HandleAsync(PaymentEventContext context, CancellationToken cancellationToken = default)
    {
        var request = context.Request;
        
        // Find the Add-on plan
        var plan = await _unitOfWork.Plans.FirstOrDefaultAsync(
            p => p.Slug.ToLower() == request.PlanSlug.ToLower() && p.DeletedAt == null,
            cancellationToken);

        if (plan is null)
        {
            _logger.LogError("Add-on Plan not found for payment: {PlanSlug}", request.PlanSlug);
            return Result.Failure(ApiMessageConstants.ErrorMessages.BillingPlanNotFound, ErrorCodes.BillingPlanNotFound);
        }

        if (context.ParsedPaymentStatus != PaymentConstants.PaymentStatuses.Paid)
        {
            return Result.Success();
        }

        // Find current active subscription
        var activeSub = await _unitOfWork.SubscriptionRepository.FirstOrDefaultAsync(
            s => s.WorkspaceId == context.WorkspaceId && s.IsActive && s.DeletedAt == null,
            cancellationToken);

        if (activeSub is null)
        {
            _logger.LogWarning("Workspace {WorkspaceId} attempted to buy Add-on but has no active subscription.", context.WorkspaceId);
            // According to our plan, if no active sub, we fail it.
            return Result.Failure(ApiMessageConstants.ErrorMessages.BillingSubscriptionNotFound, ErrorCodes.BillingSubscriptionNotFound);
        }

        // Increment Credits
        activeSub.CreditsRemaining += plan.CreditsPerCycle;

        // If service was suspended due to overage, we can resume it if credits are now positive
        if (activeSub.ServiceState == SubscriptionConstants.ServiceStates.Suspended &&
            activeSub.SuspendedReason == SubscriptionConstants.SuspendedReasons.OverageCap)
        {
            if (activeSub.CreditsRemaining > 0)
            {
                activeSub.ResumeAiService();
            }
        }

        _unitOfWork.SubscriptionRepository.Update(activeSub);

        // Record a transaction for the topup
        var topupTx = CreditMapper.CreateStripeSubscriptionTransaction(
            new StripeSubscriptionTransactionRequest(
                activeSub,
                plan,
                request.PaymentType,
                context.UserId,
                context.PaymentId));

        await _unitOfWork.CreditTransactionRepository.AddAsync(topupTx, cancellationToken);
        
        context.Subscription = activeSub;
        context.SubscriptionChanged = true;

        return Result.Success();
    }
}
