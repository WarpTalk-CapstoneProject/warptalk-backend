using System.Globalization;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Domain.Services;

namespace WarpTalk.BillingService.Application.Services;

/// <summary>
/// WT-878: THE ONE PLACE a cycle renewal moves credits. Both renewal paths — the invoice cycle close
/// (BillingCycleClosingService) and the Stripe renewal (SubscriptionPaymentEventHandler) — call it,
/// so a card customer and an invoice customer renew identically.
///
/// It reads how much of the balance was purchased (the same ledger rule CreditFreezeService uses at
/// expiry), lets <see cref="ISubscriptionDomainService.RenewCycle"/> apply the rollover cap to the
/// plan leftover only, and — when the cap removed anything — stages an explicit
/// <c>credit_forfeit</c> ledger row, so the balance never drops without a line saying why.
///
/// Stages only; the caller saves (and writes the cycle's grant row after this).
/// </summary>
public static class CycleRenewalCredits
{
    public static async Task<CycleRenewalOutcome> RenewAsync(
        IUnitOfWork unitOfWork,
        ISubscriptionDomainService domainService,
        Subscription subscription,
        string forfeitIdempotencyKey,
        DateTime nowUtc,
        CancellationToken ct)
    {
        // Only a positive balance can hold purchased credits worth protecting; skip the ledger read
        // otherwise (a zero or negative balance carries nothing either way).
        var purchased = subscription.CreditsRemaining > 0
            ? await CreditFreezeService.PurchasedCreditsAsync(unitOfWork, subscription.Id, ct)
            : 0L;

        var outcome = domainService.RenewCycle(subscription, purchased);

        if (outcome.Forfeited > 0)
        {
            await unitOfWork.CreditTransactionRepository.AddAsync(new CreditTransaction
            {
                Id = Guid.NewGuid(),
                SubscriptionId = subscription.Id,
                WorkspaceId = subscription.WorkspaceId,
                UserId = subscription.UserId,
                Amount = -outcome.Forfeited,
                Type = TransactionConstants.TransactionTypes.CreditForfeit,
                Description = string.Create(CultureInfo.InvariantCulture,
                    $"Plan credits forfeited at renewal: {outcome.Forfeited:N0} above the plan's rollover cap of {outcome.RolloverCap:N0} ({outcome.PurchasedCarried:N0} purchased credits carried over whole)"),
                ReferenceId = subscription.Id,
                ReferenceType = TransactionConstants.ReferenceTypes.CycleRollover,
                // The balance between the forfeit and the new cycle's grant, so the ledger reads
                // forfeit → grant with every step adding up.
                BalanceAfter = outcome.BalanceAfterForfeit,
                IdempotencyKey = forfeitIdempotencyKey,
                CreatedAt = nowUtc,
            }, ct);
        }

        return outcome;
    }

    /// <summary>The forfeit key of an invoice-closed cycle: the row and the period end it closed.</summary>
    public static string CycleCloseForfeitKey(Guid subscriptionId, DateTime closedPeriodEnd) =>
        string.Create(CultureInfo.InvariantCulture, $"rollover_forfeit:{subscriptionId}:{closedPeriodEnd.Ticks}");

    /// <summary>The forfeit key of a Stripe renewal: the invoice that paid for it.</summary>
    public static string StripeInvoiceForfeitKey(string stripeInvoiceId) =>
        $"stripe_invoice:{stripeInvoiceId}:rollover_forfeit";
}
