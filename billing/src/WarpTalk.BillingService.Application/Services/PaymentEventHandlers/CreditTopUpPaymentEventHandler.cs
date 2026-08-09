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
            return Result.Failure(ApiMessageConstants.ErrorMessages.BillingSubscriptionNotFound, ErrorCodes.BillingSubscriptionNotFound);
        }

        // Calculate credits based on tiers
        int creditsToAdd = CalculateCreditsFromAmount(request.Amount);

        // Increment Credits
        activeSub.CreditsRemaining += creditsToAdd;

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
        var topupTx = new CreditTransaction
        {
            Id = Guid.NewGuid(),
            SubscriptionId = activeSub.Id,
            UserId = context.UserId,
            WorkspaceId = activeSub.WorkspaceId,
            Amount = creditsToAdd,
            Type = TransactionConstants.TransactionTypes.TopUp,
            Description = $"Credit Top-up ({creditsToAdd:N0} credits)",
            ReferenceId = context.PaymentId,
            ReferenceType = TransactionConstants.ReferenceTypes.StripePayment,
            BalanceAfter = activeSub.CreditsRemaining,
            CreatedAt = DateTime.UtcNow
        };

        await _unitOfWork.CreditTransactionRepository.AddAsync(topupTx, cancellationToken);
        
        context.Subscription = activeSub;
        context.SubscriptionChanged = true;

        return Result.Success();
    }

    private int CalculateCreditsFromAmount(decimal amount)
    {
        if (amount >= 50000m * 8m) return (int)(amount / 8m);
        if (amount >= 25000m * 8.5m) return (int)(amount / 8.5m);
        if (amount >= 10000m * 9m) return (int)(amount / 9m);
        return (int)(amount / 10m);
    }
}
