using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Mappers;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Domain.Services;
using WarpTalk.BillingService.Infrastructure.Helpers;
using WarpTalk.BillingService.Infrastructure.Logging;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Infrastructure.Services;

public sealed class BillingCycleClosingService : IBillingCycleClosingService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ISubscriptionDomainService _domainService;
    private readonly IBillingPolicyService _billingPolicyService;
    private readonly ILogger<BillingCycleClosingService> _logger;

    public BillingCycleClosingService(
        IUnitOfWork unitOfWork,
        ISubscriptionDomainService domainService,
        IBillingPolicyService billingPolicyService,
        ILogger<BillingCycleClosingService> logger)
    {
        _unitOfWork = unitOfWork;
        _domainService = domainService;
        _billingPolicyService = billingPolicyService;
        _logger = logger;
    }

    public async Task<Result<int>> CloseDueCyclesAsync(
        DateTime now,
        TimeSpan lookback,
        CancellationToken cancellationToken = default)
    {
        var dueSubscriptions = await _unitOfWork.SubscriptionRepository.GetDueForRenewalAsync(
            now,
            now.Subtract(lookback),
            cancellationToken);

        var closed = 0;
        foreach (var subscription in dueSubscriptions)
        {
            // A refused subscription is left untouched and due, so it is picked up again once its
            // terms are fixed; one bad contract must not stop every other workspace's renewal.
            if (await CloseOneCycleAsync(subscription, now, cancellationToken) is null)
                closed++;
        }

        if (closed > 0)
            await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(closed);
    }

    public async Task<Result<int>> CloseWorkspaceCycleAsync(
        Guid workspaceId,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        var subscription = await _unitOfWork.SubscriptionRepository.FirstOrDefaultAsync(
            s => s.WorkspaceId == workspaceId &&
                 s.IsActive &&
                 s.DeletedAt == null &&
                 s.AutoRenew &&
                 s.Status == SubscriptionConstants.SubscriptionStatuses.Active,
            "Plan",
            cancellationToken);

        if (subscription is null)
        {
            return Result.Failure<int>(
                ApiMessageConstants.ErrorMessages.BillingSubscriptionNotFound,
                ErrorCodes.BillingSubscriptionNotFound);
        }

        var originalPeriodEnd = subscription.CurrentPeriodEnd;
        subscription.CurrentPeriodEnd = now.AddMinutes(-1);
        var refusal = await CloseOneCycleAsync(subscription, now, cancellationToken);
        if (refusal is not null)
        {
            subscription.CurrentPeriodEnd = originalPeriodEnd;
            return Result.Failure<int>(refusal, ErrorCodes.BillingSubscriptionConflict);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(1);
    }

    /// <returns>Null when the cycle was closed; otherwise why it was refused, with nothing written.</returns>
    private async Task<string?> CloseOneCycleAsync(
        Subscription subscription,
        DateTime now,
        CancellationToken cancellationToken)
    {
        // #466: the cycle close owns invoice rows only. The query already says so; this is the
        // guard for the manual close-now path and for a row that changed owner between the select
        // and here (the xmin token then fails the save rather than granting twice).
        if (subscription.RenewalMode != SubscriptionConstants.RenewalModes.Invoice)
        {
            return $"Subscription {subscription.Id} renews through {subscription.RenewalMode}, not an invoice.";
        }

        var plan = subscription.Plan ?? throw new InvalidOperationException("Billing cycle close requires subscription.Plan to be loaded.");
        var creditsPerCycle = subscription.CreditsPerCycleOverride ?? plan.CreditsPerCycle;
        var invoiceTermsDays = subscription.InvoiceTermsDaysOverride ?? plan.InvoiceTermsDays;

        // The currency follows the amounts it labels (see BillingCycleCharge): a contract price is
        // VND whatever the plan is priced in.
        var resolution = BillingCycleCharge.Resolve(subscription, plan);
        if (resolution.Charge is not { } charge)
        {
            _logger.LogError(
                BillingOperationalEventIds.BillingCycleCurrencyMismatch,
                "billing_cycle_refused_currency_mismatch WorkspaceId={WorkspaceId} SubscriptionId={SubscriptionId} PlanCurrency={PlanCurrency} Detail={Detail}",
                subscription.WorkspaceId,
                subscription.Id,
                plan.Currency,
                resolution.CurrencyMismatch);
            return resolution.CurrencyMismatch;
        }

        var contractPrice = charge.BasePrice;
        var overageCredits = charge.OverageCredits;
        var overagePricePerCredit = charge.OveragePricePerCredit;
        var overageAmount = charge.OverageAmount;
        var subtotal = charge.Subtotal;
        var billingPolicy = await _billingPolicyService.GetPolicyAsync(cancellationToken);
        var tax = Math.Round(subtotal * billingPolicy.VatRate, 2, MidpointRounding.AwayFromZero);
        var total = subtotal + tax;
        var usageBreakdown = await GetUsageBreakdownAsync(subscription, cancellationToken);

        var payment = PaymentMapper.CreateBillingCyclePayment(new BillingCyclePaymentCreationRequest(
            subscription,
            charge.Currency,
            subtotal,
            tax,
            total,
            overageCredits,
            now));
        await _unitOfWork.PaymentRepository.AddAsync(payment, cancellationToken);

        var invoice = InvoiceMapper.CreateBillingCycleInvoice(new BillingCycleInvoiceCreationRequest(
            subscription,
            plan,
            payment.Id,
            charge.Currency,
            contractPrice,
            overageCredits,
            overagePricePerCredit,
            overageAmount,
            usageBreakdown,
            subtotal,
            tax,
            total,
            invoiceTermsDays,
            now));
        await _unitOfWork.InvoiceRepository.AddAsync(invoice, cancellationToken);

        var (newStart, newEnd) = SubscriptionRenewalHelper.CalculateNextCycleDates(subscription.CurrentPeriodEnd, plan.BillingCycle);

        _domainService.RenewCycle(subscription);
        subscription.CurrentPeriodStart = newStart;
        subscription.CurrentPeriodEnd = newEnd;
        subscription.UpdatedAt = now;
        _unitOfWork.SubscriptionRepository.Update(subscription);

        var renewalTx = subscription.CreateRenewalTransaction(plan, newStart);
        renewalTx.Amount = creditsPerCycle;
        renewalTx.BalanceAfter = subscription.CreditsRemaining;
        renewalTx.ReferenceId = invoice.Id;
        renewalTx.ReferenceType = TransactionConstants.ReferenceTypes.Payment;
        await _unitOfWork.CreditTransactionRepository.AddAsync(renewalTx, cancellationToken);
        return null;
    }

    private async Task<IReadOnlyCollection<BillingCycleUsageBreakdownItem>> GetUsageBreakdownAsync(
        Subscription subscription,
        CancellationToken cancellationToken)
    {
        var usageRecords = await _unitOfWork.UsageRecordRepository.FindAsync(
            u => u.SubscriptionId == subscription.Id &&
                 u.RecordedAt >= subscription.CurrentPeriodStart &&
                 u.RecordedAt < subscription.CurrentPeriodEnd,
            cancellationToken);

        return usageRecords
            .GroupBy(u => new { ChargeType = u.UsageType, u.Unit })
            .Select(g => new BillingCycleUsageBreakdownItem(
                g.Key.ChargeType,
                g.Key.Unit,
                g.Sum(u => u.Quantity),
                g.Sum(u => u.CreditsConsumed)))
            .OrderBy(i => i.ChargeType)
            .ThenBy(i => i.Unit)
            .ToArray();
    }
}
