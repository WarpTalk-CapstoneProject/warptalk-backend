using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Application.Services;

/// <summary>
/// What one billing cycle charges, and in which currency — decided per amount, not per plan.
///
/// THE BUG THIS EXISTS TO STOP
///   The cycle invoice took its amount from the contract price or <c>plan.Price</c> and its currency
///   from <c>plan.Currency</c>. On a USD plan with a (then VND) contract price, 1,900,000 VND went
///   out as 1,900,000 USD. <see cref="AdminSubscriptionRevenue.MonthlyAmount"/> already refused to
///   read the plan's currency for a contract price; the invoice did not.
///
/// Each amount carries the currency of the place it was read from:
///   - <c>ContractPriceUsd</c> and <c>OveragePricePerCreditOverride</c> are contract terms, and
///     both are in USD, the accounting currency (the column name says so for one; the
///     contract-terms editor labels the other "USD per credit").
///   - <c>plan.Price</c> and <c>plan.OveragePricePerCredit</c> are in <c>plan.Currency</c>.
///
/// The overage only counts when overage credits were used: a USD plan's catalog overage price
/// that multiplies zero credits is not a second currency on the invoice.
///
/// When the contributing amounts disagree there is no honest invoice — an invoice is not a report,
/// it has no business picking an exchange rate, and adding VND to USD produces a number in neither. That is returned as a
/// mismatch for the caller to refuse, never summed.
/// </summary>
public sealed record BillingCycleCharge(
    string Currency,
    decimal BasePrice,
    int OverageCredits,
    decimal OveragePricePerCredit,
    decimal OverageAmount)
{
    public decimal Subtotal => BasePrice + OverageAmount;

    public static BillingCycleChargeResolution Resolve(Subscription subscription, Plan plan)
    {
        var contractCurrency = PaymentConstants.Currencies.UsdAccounting;

        var (basePrice, baseCurrency) = subscription.ContractPriceUsd is { } contractPrice
            ? (contractPrice, contractCurrency)
            : (plan.Price, plan.Currency);

        var (overagePrice, overageCurrency) = subscription.OveragePricePerCreditOverride is { } overrideRate
            ? (overrideRate, contractCurrency)
            : (plan.OveragePricePerCredit, plan.Currency);

        var overageCredits = subscription.OverageCreditsThisCycle;
        var overageAmount = overageCredits * overagePrice;

        if (overageCredits > 0 && !SameCurrency(baseCurrency, overageCurrency))
        {
            return new BillingCycleChargeResolution(
                null,
                $"Cycle charge mixes currencies: base price in {baseCurrency}, overage in {overageCurrency}.");
        }

        // Keep the plan's own spelling ("vnd" vs "VND") when the charge is in the plan's currency,
        // so invoices from one plan do not start disagreeing about how they write it.
        var currency = SameCurrency(baseCurrency, plan.Currency) ? plan.Currency : baseCurrency;

        return new BillingCycleChargeResolution(
            new BillingCycleCharge(currency, basePrice, overageCredits, overagePrice, overageAmount),
            null);
    }

    private static bool SameCurrency(string? left, string? right)
        => string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
}

public sealed record BillingCycleChargeResolution(BillingCycleCharge? Charge, string? CurrencyMismatch);
