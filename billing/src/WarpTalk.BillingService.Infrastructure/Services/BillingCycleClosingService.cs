using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Mappers;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Domain.Services;
using WarpTalk.BillingService.Infrastructure.Helpers;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Infrastructure.Services;

public sealed class BillingCycleClosingService : IBillingCycleClosingService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ISubscriptionDomainService _domainService;
    private readonly IBillingPolicyService _billingPolicyService;

    public BillingCycleClosingService(
        IUnitOfWork unitOfWork,
        ISubscriptionDomainService domainService,
        IBillingPolicyService billingPolicyService)
    {
        _unitOfWork = unitOfWork;
        _domainService = domainService;
        _billingPolicyService = billingPolicyService;
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

        foreach (var subscription in dueSubscriptions)
        {
            await CloseOneCycleAsync(subscription, now, cancellationToken);
        }

        if (dueSubscriptions.Count > 0)
            await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(dueSubscriptions.Count);
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

        subscription.CurrentPeriodEnd = now.AddMinutes(-1);
        await CloseOneCycleAsync(subscription, now, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(1);
    }

    private async Task CloseOneCycleAsync(
        Subscription subscription,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var plan = subscription.Plan ?? throw new InvalidOperationException("Billing cycle close requires subscription.Plan to be loaded.");
        var creditsPerCycle = subscription.CreditsPerCycleOverride ?? plan.CreditsPerCycle;
        var overagePricePerCredit = subscription.OveragePricePerCreditOverride ?? plan.OveragePricePerCredit;
        var invoiceTermsDays = subscription.InvoiceTermsDaysOverride ?? plan.InvoiceTermsDays;
        var contractPrice = subscription.ContractPriceVnd ?? plan.Price;
        var overageCredits = subscription.OverageCreditsThisCycle;
        var overageAmount = overageCredits * overagePricePerCredit;
        var subtotal = contractPrice + overageAmount;
        var billingPolicy = await _billingPolicyService.GetPolicyAsync(cancellationToken);
        var tax = Math.Round(subtotal * billingPolicy.VatRate, 2, MidpointRounding.AwayFromZero);
        var total = subtotal + tax;
        var usageBreakdown = await GetUsageBreakdownAsync(subscription, cancellationToken);

        var payment = PaymentMapper.CreateBillingCyclePayment(new BillingCyclePaymentCreationRequest(
            subscription,
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
