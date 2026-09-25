using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;

namespace WarpTalk.BillingService.Application.Services;

public interface ICreditPackExpiryService
{
    /// <summary>Expires every due pack purchase (a bounded batch). Returns how many were swept.</summary>
    Task<int> SweepAsync(CancellationToken ct = default);
}

/// <summary>
/// G11 — removes the unspent part of a credit pack when its validity ends.
///
/// The balance is one number on the subscription; credits carry no lot. So "unspent" needs a rule,
/// and the rule here is the one most favourable to the customer: a pack's credits are treated as
/// spent FIRST. Everything the workspace consumed since the purchase counts against the pack, and
/// only what is left of the pack after that — and never more than the balance — expires:
///
///     expired = clamp(pack credits − credits consumed since purchase, 0, current balance)
///
/// Overlapping packs each count the same consumption, which can only under-expire, never take
/// credits a customer is owed. Each expiry is one <c>adjustment</c> ledger row referencing the
/// purchase, so the balance history explains itself.
/// </summary>
public sealed class CreditPackExpiryService : ICreditPackExpiryService
{
    private const int BatchSize = 100;

    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<CreditPackExpiryService> _logger;
    private readonly TimeProvider _time;

    public CreditPackExpiryService(IUnitOfWork unitOfWork, ILogger<CreditPackExpiryService> logger, TimeProvider? time = null)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public async Task<int> SweepAsync(CancellationToken ct = default)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var due = await _unitOfWork.CreditPackPurchases.GetDueForExpiryAsync(now, BatchSize, ct);
        var swept = 0;

        foreach (var purchase in due)
        {
            var consumed = (await _unitOfWork.CreditTransactionRepository.GetWorkspaceConsumptionTotalsAsync(
                purchase.WorkspaceId, purchase.PurchasedAt, purchase.ExpiresAt ?? now, ct)).CreditsConsumed;

            var subscription = await _unitOfWork.SubscriptionRepository.GetByIdAsync(purchase.SubscriptionId, ct);
            var expired = ExpiredCredits(purchase.TotalCredits, consumed, subscription?.CreditsRemaining ?? 0);

            if (expired > 0 && subscription is not null)
            {
                subscription.CreditsRemaining -= expired;
                subscription.UpdatedAt = now;
                _unitOfWork.SubscriptionRepository.Update(subscription);

                await _unitOfWork.CreditTransactionRepository.AddAsync(new CreditTransaction
                {
                    Id = Guid.NewGuid(),
                    SubscriptionId = subscription.Id,
                    WorkspaceId = purchase.WorkspaceId,
                    UserId = purchase.UserId,
                    Amount = -expired,
                    Type = TransactionConstants.TransactionTypes.Adjustment,
                    Description = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Credit pack expired: {expired:N0} unspent credits removed"),
                    ReferenceId = purchase.Id,
                    ReferenceType = PackageCatalogConstants.ReferenceTypes.CreditPackExpiry,
                    BalanceAfter = subscription.CreditsRemaining,
                    CreatedAt = now,
                }, ct);
            }

            purchase.ExpiredCredits = expired;
            purchase.ExpiredAt = now;
            _unitOfWork.CreditPackPurchases.Update(purchase);
            await _unitOfWork.SaveChangesAsync(ct);
            swept++;

            _logger.LogInformation(
                "credit_pack_expired: Purchase={PurchaseId} WorkspaceId={WorkspaceId} Pack={Credits} ConsumedSince={Consumed} Expired={Expired}",
                purchase.Id, purchase.WorkspaceId, purchase.TotalCredits, consumed, expired);
        }

        return swept;
    }

    /// <summary>The customer-favourable rule in the class summary, as a pure function.</summary>
    public static int ExpiredCredits(int packCredits, long consumedSincePurchase, int currentBalance)
    {
        var unspent = Math.Max(0L, packCredits - Math.Max(0L, consumedSincePurchase));
        return (int)Math.Clamp(unspent, 0L, Math.Max(0, currentBalance));
    }
}
