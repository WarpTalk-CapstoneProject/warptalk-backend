using System.Globalization;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Services.PaymentEventHandlers;

/// <summary>
/// G11 — grants a paid catalog credit pack.
///
/// The same ledger path as the custom top-up (WT-429): credits onto the workspace's subscription
/// balance, one <c>top_up</c> ledger row, the entitlement refresh. What it adds is the purchase
/// row the admin's sales figures and the expiry sweep read, keyed by the Stripe session so the
/// webhook and the return page — which both run for every session — cannot grant twice.
///
/// HOW MANY CREDITS: the count written on the session at checkout (base + bonus), not the pack's
/// current numbers — an admin editing the pack between checkout and completion must not change
/// what an already-authorised payment buys.
///
/// Every status of a CreditPack payment is claimed here, including refunds, so none of them can
/// fall through to CancellationPaymentEventHandler, which would cancel the workspace's PLAN.
/// </summary>
public sealed class CreditPackPaymentEventHandler : IPaymentEventHandler
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<CreditPackPaymentEventHandler> _logger;

    public CreditPackPaymentEventHandler(IUnitOfWork unitOfWork, ILogger<CreditPackPaymentEventHandler> logger)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public bool CanHandle(PaymentEventContext context) =>
        string.Equals(context.Request.PaymentType, PaymentConstants.PaymentTypes.CreditPack, StringComparison.OrdinalIgnoreCase);

    public async Task<Result> HandleAsync(PaymentEventContext context, CancellationToken cancellationToken = default)
    {
        if (context.ParsedPaymentStatus != PaymentConstants.PaymentStatuses.Paid)
        {
            if (context.ParsedPaymentStatus == PaymentConstants.PaymentStatuses.Refunded)
            {
                // A refund does not claw credits back automatically: some may already be spent,
                // and taking a balance negative is a support decision (admin credit adjustment).
                _logger.LogWarning(
                    "credit_pack_refunded: WorkspaceId={WorkspaceId} Payment={Payment}. Credits were NOT removed; adjust by hand if needed.",
                    context.WorkspaceId, context.ProviderTransactionId);
            }

            return Result.Success();
        }

        var sessionId = context.Request.StripeSessionId;
        if (!string.IsNullOrWhiteSpace(sessionId)
            && await _unitOfWork.CreditPackPurchases.ExistsForSessionAsync(sessionId, cancellationToken))
        {
            return Result.Success();
        }

        if (!Guid.TryParse(context.Request.PackageId, out var packId))
        {
            _logger.LogError("credit_pack_missing_package: StripeSessionId={SessionId} WorkspaceId={WorkspaceId}", sessionId, context.WorkspaceId);
            return Result.Failure(PackageCatalogConstants.Errors.NotFound, ErrorCodes.ValidationError);
        }

        // Archived packs are still found: archiving stops new sales, not the completion of one
        // already paid for.
        var pack = await _unitOfWork.CreditPacks.GetByIdAsync(packId, cancellationToken);
        if (pack is null)
        {
            _logger.LogError("credit_pack_not_found: PackageId={PackageId} StripeSessionId={SessionId}", packId, sessionId);
            return Result.Failure(PackageCatalogConstants.Errors.NotFound, ErrorCodes.ValidationError);
        }

        var totalCredits = context.Request.Credits > 0 ? context.Request.Credits : pack.TotalCredits;
        var bonus = Math.Min(pack.BonusCredits, totalCredits);
        var baseCredits = totalCredits - bonus;

        var subscription = context.Subscription
            ?? await _unitOfWork.SubscriptionRepository.FirstOrDefaultAsync(
                s => s.WorkspaceId == context.WorkspaceId && s.IsActive && s.DeletedAt == null, cancellationToken);
        if (subscription is null)
        {
            _logger.LogError(
                "credit_pack_no_subscription: StripeSessionId={SessionId} WorkspaceId={WorkspaceId}. Paid, and nothing to credit.",
                sessionId, context.WorkspaceId);
            return Result.Failure(BillingMessageConstants.ErrorMessages.CreditTopUpNoSubscription, ErrorCodes.InvalidState);
        }

        var now = DateTime.UtcNow;
        var purchaseId = Guid.NewGuid();
        subscription.CreditsRemaining += totalCredits;
        subscription.UpdatedAt = now;
        _unitOfWork.SubscriptionRepository.Update(subscription);

        await _unitOfWork.CreditTransactionRepository.AddAsync(new CreditTransaction
        {
            Id = Guid.NewGuid(),
            SubscriptionId = subscription.Id,
            WorkspaceId = context.WorkspaceId,
            UserId = context.UserId,
            Amount = totalCredits,
            Type = TransactionConstants.TransactionTypes.TopUp,
            Description = bonus > 0
                ? string.Format(CultureInfo.InvariantCulture, "Credit pack '{0}': {1:N0} credits + {2:N0} bonus", pack.Name, baseCredits, bonus)
                : string.Format(CultureInfo.InvariantCulture, "Credit pack '{0}': {1:N0} credits", pack.Name, baseCredits),
            ReferenceId = purchaseId,
            ReferenceType = PackageCatalogConstants.ReferenceTypes.CreditPackPurchase,
            BalanceAfter = subscription.CreditsRemaining,
            Currency = context.Request.Currency,
            CreatedAt = now,
        }, cancellationToken);

        var listPrice = context.Request.ListPrice > 0 ? context.Request.ListPrice : context.Request.Amount;
        await _unitOfWork.CreditPackPurchases.AddAsync(new CreditPackPurchase
        {
            Id = purchaseId,
            CreditPackId = pack.Id,
            WorkspaceId = context.WorkspaceId,
            UserId = context.UserId,
            SubscriptionId = subscription.Id,
            PaymentId = context.PaymentId,
            StripeSessionId = string.IsNullOrWhiteSpace(sessionId) ? context.ProviderTransactionId : sessionId,
            Credits = baseCredits,
            BonusCredits = bonus,
            Currency = (context.Request.Currency ?? string.Empty).ToLowerInvariant(),
            AmountPaid = context.Request.Amount,
            CouponId = Guid.TryParse(context.Request.CouponId, out var couponId) ? couponId : null,
            DiscountAmount = Math.Max(0, listPrice - context.Request.Amount),
            PurchasedAt = now,
            ExpiresAt = pack.ValidityDays is { } days ? now.AddDays(days) : null,
        }, cancellationToken);

        context.Subscription = subscription;
        context.SubscriptionChanged = true;

        _logger.LogInformation(
            "credit_pack_granted: Pack={Slug} Credits={Credits} WorkspaceId={WorkspaceId} BalanceAfter={BalanceAfter}",
            pack.Slug, totalCredits, context.WorkspaceId, subscription.CreditsRemaining);
        return Result.Success();
    }
}
