using System.Globalization;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;

namespace WarpTalk.BillingService.Application.Services;

/// <summary>
/// How an ended subscription's balance divides. <see cref="Frozen"/> is kept, <see cref="Forfeited"/>
/// is removed; the two always add up to <see cref="Balance"/> when the balance is positive.
/// </summary>
public readonly record struct CreditExpirySplit(int Balance, int PurchasedKept, int PlanKept, int Forfeited)
{
    public int Frozen => PurchasedKept + PlanKept;
}

/// <summary>
/// The frozen-credit grace window: a platform setting (the settings console, billing category) and
/// the default that applies when nobody has set it.
/// </summary>
public static class FrozenCreditDefaults
{
    /// <summary>Days an ended subscription's frozen credits wait for a renewal before turning dormant.</summary>
    public const string GraceDaysKey = WarpTalk.Shared.PlatformSettings.PlatformSettingsCatalog.FrozenCreditGraceDays;

    public const int GraceDays = 30;
}

public interface ICreditFreezeService
{
    /// <summary>
    /// Freezes (and, above the rollover cap, forfeits) the balance of every subscription that has
    /// ended and not been split yet. Saves per subscription. Returns how many were split.
    /// </summary>
    Task<int> SplitEndedSubscriptionsAsync(DateTime nowUtc, CancellationToken ct = default);

    /// <summary>
    /// Moves every frozen balance whose workspace has a live subscription again into that
    /// subscription. Saves per release. Returns how many were released.
    /// </summary>
    Task<int> ReleaseFrozenCreditsAsync(DateTime nowUtc, CancellationToken ct = default);

    /// <summary>
    /// Stages (does NOT save) the release of every frozen balance of <paramref name="target"/>'s
    /// workspace into <paramref name="target"/>. For the payment path, so the credits come back in
    /// the same commit as the renewal that earned them. Returns the credits released.
    /// </summary>
    Task<int> StageReleaseIntoAsync(Subscription target, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>Marks frozen balances older than the grace window as dormant. Never deletes them.</summary>
    Task<int> MarkDormantAsync(DateTime nowUtc, int graceDays, CancellationToken ct = default);
}

/// <summary>
/// WHAT HAPPENS TO CREDITS WHEN A SUBSCRIPTION ENDS WITHOUT A RENEWAL.
///
/// Before this, nothing did. The expiry sweep set is_active = false and left the balance on the row,
/// and every consumer of "the workspace's subscription" filters on is_active — so the balance was
/// invisible, unspendable, and, because a renewal through checkout creates a NEW row, never came
/// back. Purchased credits were lost without a ledger line saying so.
///
/// THE POLICY (owner default, 2026-09-25)
///   * Purchased credits — top-ups and credit packs — and credits an admin granted by hand are
///     FROZEN: kept, not spendable, shown to the owner, and released into the live subscription on
///     renewal. A frozen credit pack still expires on its own date (CreditPackExpiryService).
///   * Plan-included credits follow the plan's own end-of-period rule, which is the one
///     SubscriptionDomainService.RenewCycle applies at every cycle close: up to
///     plans.rollover_cap_credits carries over, the rest is forfeited. Here "carries over" means
///     frozen with the rest, and the forfeit is a ledger row, not a silent overwrite.
///   * After the grace window (platform setting billing.frozen_credits.grace_days, default 30) without a
///     renewal, frozen credits are marked DORMANT — still kept, still shown, still released by a
///     renewal. Nothing here ever deletes paid credit; an admin can adjust it through the audited
///     Adjust Credit action.
///
/// WHICH CREDITS WERE PURCHASED
///   The balance is one number; credits carry no lot. So the purchased part needs a rule, and the
///   rule is the one most favourable to the customer at THIS moment: purchased credits are spent
///   LAST. Of the balance left, up to the total purchased (and granted) through this subscription is
///   treated as purchased; only what exceeds that is plan-included. That is deliberately the mirror
///   of CreditPackExpiryService, which treats a pack as spent FIRST when the pack expires — each rule
///   is the customer-favourable reading of its own event.
///
/// EVERY MOVE IS A LEDGER ROW WITH AN IDEMPOTENCY KEY
///   credit_forfeit / credit_freeze are keyed on the subscription, credit_unfreeze on the row the
///   credits came from; ux_credit_transactions_idempotency_key makes a second write impossible even
///   if the credits_frozen_at marker were ever lost.
/// </summary>
public sealed class CreditFreezeService : ICreditFreezeService
{
    private const int BatchSize = 100;

    private static readonly string TopUpDescriptionPrefix =
        BillingMessageConstants.SuccessMessages.CreditTopUpGrantedTemplate.Split('{')[0];

    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<CreditFreezeService> _logger;

    public CreditFreezeService(IUnitOfWork unitOfWork, ILogger<CreditFreezeService> logger)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    /// <summary>The policy in the class summary, as a pure function.</summary>
    public static CreditExpirySplit Split(int balance, long purchasedCredits, int rolloverCap)
    {
        if (balance <= 0)
        {
            return new CreditExpirySplit(balance, 0, 0, 0);
        }

        var purchasedKept = (int)Math.Clamp(purchasedCredits, 0L, balance);
        var planPart = balance - purchasedKept;
        var planKept = Math.Clamp(rolloverCap, 0, planPart);
        return new CreditExpirySplit(balance, purchasedKept, planKept, planPart - planKept);
    }

    public async Task<int> SplitEndedSubscriptionsAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var ended = await _unitOfWork.SubscriptionRepository.FindAsync(
            s => !s.IsActive
                 && s.DeletedAt == null
                 && s.CreditsFrozenAt == null
                 && (s.Status == SubscriptionConstants.SubscriptionStatuses.Expired
                     || s.Status == SubscriptionConstants.SubscriptionStatuses.Cancelled),
            ct);

        var split = 0;
        foreach (var subscription in ended.OrderBy(s => s.CurrentPeriodEnd).Take(BatchSize))
        {
            ct.ThrowIfCancellationRequested();
            await SplitOneAsync(subscription, nowUtc, ct);
            await _unitOfWork.SaveChangesAsync(ct);
            split++;
        }

        return split;
    }

    private async Task SplitOneAsync(Subscription subscription, DateTime nowUtc, CancellationToken ct)
    {
        var balance = subscription.CreditsRemaining;
        var plan = await _unitOfWork.Plans.GetByIdAsync(subscription.PlanId, ct);
        var rolloverCap = plan?.RolloverCapCredits ?? 0;
        var purchased = balance > 0 ? await PurchasedCreditsAsync(subscription.Id, ct) : 0L;
        var result = Split(balance, purchased, rolloverCap);

        if (result.Forfeited > 0)
        {
            subscription.CreditsRemaining -= result.Forfeited;
            await _unitOfWork.CreditTransactionRepository.AddAsync(new CreditTransaction
            {
                Id = Guid.NewGuid(),
                SubscriptionId = subscription.Id,
                WorkspaceId = subscription.WorkspaceId,
                UserId = subscription.UserId,
                Amount = -result.Forfeited,
                Type = TransactionConstants.TransactionTypes.CreditForfeit,
                Description = string.Create(CultureInfo.InvariantCulture,
                    $"Plan credits forfeited at period end: {result.Forfeited:N0} above the plan's rollover cap of {rolloverCap:N0}"),
                ReferenceId = subscription.Id,
                ReferenceType = TransactionConstants.ReferenceTypes.SubscriptionExpiry,
                BalanceAfter = subscription.CreditsRemaining,
                IdempotencyKey = $"credit_forfeit:{subscription.Id}",
                CreatedAt = nowUtc,
            }, ct);
        }

        if (result.Frozen > 0)
        {
            subscription.CreditsRemaining -= result.Frozen;
            subscription.FrozenCredits += result.Frozen;
            await _unitOfWork.CreditTransactionRepository.AddAsync(new CreditTransaction
            {
                Id = Guid.NewGuid(),
                SubscriptionId = subscription.Id,
                WorkspaceId = subscription.WorkspaceId,
                UserId = subscription.UserId,
                Amount = -result.Frozen,
                Type = TransactionConstants.TransactionTypes.CreditFreeze,
                Description = string.Create(CultureInfo.InvariantCulture,
                    $"{result.Frozen:N0} credits frozen: subscription ended ({result.PurchasedKept:N0} purchased or granted, {result.PlanKept:N0} plan rollover). Renew to use them."),
                ReferenceId = subscription.Id,
                ReferenceType = TransactionConstants.ReferenceTypes.SubscriptionExpiry,
                BalanceAfter = subscription.CreditsRemaining,
                IdempotencyKey = $"credit_freeze:{subscription.Id}",
                CreatedAt = nowUtc,
            }, ct);
        }

        subscription.CreditsFrozenAt = nowUtc;
        subscription.UpdatedAt = nowUtc;
        _unitOfWork.SubscriptionRepository.Update(subscription);

        _logger.LogInformation(
            "subscription_credits_split WorkspaceId={WorkspaceId} SubscriptionId={SubscriptionId} Balance={Balance} "
            + "PurchasedKept={PurchasedKept} PlanKept={PlanKept} Forfeited={Forfeited} RolloverCap={RolloverCap}",
            subscription.WorkspaceId, subscription.Id, balance, result.PurchasedKept, result.PlanKept, result.Forfeited, rolloverCap);
    }

    /// <summary>
    /// Credits bought or granted through this subscription, net of what already expired: credit
    /// packs (minus their expiries), credit top-ups, frozen credits released into it, and manual
    /// admin adjustments. Clamped at zero by <see cref="Split"/>.
    /// </summary>
    private async Task<long> PurchasedCreditsAsync(Guid subscriptionId, CancellationToken ct)
    {
        var rows = await _unitOfWork.CreditTransactionRepository.FindAsync(
            t => t.SubscriptionId == subscriptionId && t.Type != TransactionConstants.TransactionTypes.Consume,
            ct);

        return rows.Where(IsPurchasedOrGranted).Sum(t => (long)t.Amount);
    }

    private static bool IsPurchasedOrGranted(CreditTransaction t) =>
        t.ReferenceType == PackageCatalogConstants.ReferenceTypes.CreditPackPurchase
        || t.ReferenceType == PackageCatalogConstants.ReferenceTypes.CreditPackExpiry
        || t.ReferenceType == TransactionConstants.ReferenceTypes.ManualAdjustment
        || t.Type == TransactionConstants.TransactionTypes.CreditUnfreeze
        || (t.Type == TransactionConstants.TransactionTypes.TopUp
            && t.Description != null
            && t.Description.StartsWith(TopUpDescriptionPrefix, StringComparison.Ordinal));

    public async Task<int> ReleaseFrozenCreditsAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var frozen = await _unitOfWork.SubscriptionRepository.FindAsync(
            s => s.FrozenCredits > 0 && s.DeletedAt == null,
            ct);

        var released = 0;
        foreach (var workspaceId in frozen.Select(s => s.WorkspaceId).Distinct().Take(BatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var target = await _unitOfWork.SubscriptionRepository.FirstOrDefaultAsync(
                s => s.WorkspaceId == workspaceId && s.IsActive && s.DeletedAt == null,
                ct);
            if (target is null)
            {
                continue;
            }

            if (await StageReleaseIntoAsync(target, nowUtc, ct) > 0)
            {
                await _unitOfWork.SaveChangesAsync(ct);
                released++;
            }
        }

        return released;
    }

    public async Task<int> StageReleaseIntoAsync(Subscription target, DateTime nowUtc, CancellationToken ct = default)
    {
        var sources = await _unitOfWork.SubscriptionRepository.FindAsync(
            s => s.WorkspaceId == target.WorkspaceId && s.FrozenCredits > 0 && s.DeletedAt == null,
            ct);

        var total = 0;
        foreach (var source in sources.OrderBy(s => s.CreditsFrozenAt))
        {
            var amount = source.FrozenCredits;
            var releasedAt = source.CreditsFrozenAt ?? nowUtc;

            source.FrozenCredits = 0;
            source.FrozenCreditsDormantAt = null;
            source.UpdatedAt = nowUtc;

            target.CreditsRemaining += amount;
            total += amount;

            await _unitOfWork.CreditTransactionRepository.AddAsync(new CreditTransaction
            {
                Id = Guid.NewGuid(),
                SubscriptionId = target.Id,
                WorkspaceId = target.WorkspaceId,
                UserId = target.UserId,
                Amount = amount,
                Type = TransactionConstants.TransactionTypes.CreditUnfreeze,
                Description = string.Create(CultureInfo.InvariantCulture,
                    $"{amount:N0} frozen credits restored on renewal"),
                ReferenceId = source.Id,
                ReferenceType = TransactionConstants.ReferenceTypes.FrozenCreditRelease,
                BalanceAfter = target.CreditsRemaining,
                // Keyed on the source row AND the moment it was frozen, so the one release of each
                // freeze is unique while a row that is frozen again later (reactivated, then ended)
                // can still be released again.
                IdempotencyKey = string.Create(CultureInfo.InvariantCulture,
                    $"credit_unfreeze:{source.Id}:{releasedAt.Ticks}"),
                CreatedAt = nowUtc,
            }, ct);

            _logger.LogInformation(
                "frozen_credits_released WorkspaceId={WorkspaceId} From={SourceId} Into={TargetId} Credits={Credits}",
                target.WorkspaceId, source.Id, target.Id, amount);
        }

        if (total > 0)
        {
            // The same rule CompPeriodAsync applies: credits that bring the balance back above zero
            // lift an overage suspension, and nothing else.
            if (target.CreditsRemaining > 0
                && target.ServiceState == SubscriptionConstants.ServiceStates.Suspended
                && target.SuspendedReason == SubscriptionConstants.SuspendedReasons.OverageCap)
            {
                target.ServiceState = SubscriptionConstants.ServiceStates.Healthy;
                target.SuspendedReason = null;
            }

            // No Update(target): the caller's target is already tracked — loaded by this unit of
            // work, or a subscription the payment path has just ADDED and not saved. Marking an added
            // entity Modified would turn its INSERT into an UPDATE of a row that does not exist.
            target.UpdatedAt = nowUtc;
        }

        return total;
    }

    public async Task<int> MarkDormantAsync(DateTime nowUtc, int graceDays, CancellationToken ct = default)
    {
        var candidates = await _unitOfWork.SubscriptionRepository.FindAsync(
            s => s.FrozenCredits > 0 && !s.IsActive && s.DeletedAt == null && s.FrozenCreditsDormantAt == null,
            ct);

        var cutoff = nowUtc.AddDays(-Math.Max(0, graceDays));
        var marked = 0;
        foreach (var subscription in candidates)
        {
            if (EndedAt(subscription) > cutoff)
            {
                continue;
            }

            subscription.FrozenCreditsDormantAt = nowUtc;
            subscription.UpdatedAt = nowUtc;
            _unitOfWork.SubscriptionRepository.Update(subscription);
            marked++;

            _logger.LogWarning(
                "frozen_credits_dormant WorkspaceId={WorkspaceId} SubscriptionId={SubscriptionId} FrozenCredits={FrozenCredits} GraceDays={GraceDays}. "
                + "Kept, not deleted; a renewal still restores them.",
                subscription.WorkspaceId, subscription.Id, subscription.FrozenCredits, graceDays);
        }

        if (marked > 0)
        {
            await _unitOfWork.SaveChangesAsync(ct);
        }

        return marked;
    }

    /// <summary>
    /// When the subscription stopped being usable: the earlier of its period end, its cancellation
    /// and the moment it was split. A plan superseded mid-period has a period end in the future.
    /// </summary>
    public static DateTime EndedAt(Subscription subscription)
    {
        var ended = subscription.CurrentPeriodEnd;
        if (subscription.CancelledAt is { } cancelled && cancelled < ended) ended = cancelled;
        if (subscription.CreditsFrozenAt is { } frozen && frozen < ended) ended = frozen;
        return ended;
    }
}
