using Stripe;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Infrastructure.Services;

/// <summary>
/// Reads the fee Stripe kept on one payment: the balance transaction of the charge behind the id
/// WarpTalk stored for it — a Checkout Session (cs_), a PaymentIntent (pi_) or an Invoice (in_,
/// through its invoice payments). Read-only; a key never leaves Stripe.net.
/// </summary>
public sealed class StripeFeeReader
{
    private readonly IStripeSdkClient _stripe;

    public StripeFeeReader(IStripeSdkClient stripe)
    {
        _stripe = stripe;
    }

    public async Task<PaymentProviderFee> ReadAsync(Guid paymentId, string providerTransactionId, DateTime now, CancellationToken ct)
    {
        var fee = new PaymentProviderFee
        {
            PaymentId = paymentId,
            Provider = ProviderCatalog.Stripe,
            FetchedAt = DateTime.SpecifyKind(now, DateTimeKind.Utc),
        };

        try
        {
            var paymentIntentId = await PaymentIntentOfAsync(providerTransactionId.Trim(), ct);
            if (paymentIntentId is null)
            {
                return NotFound(fee, "no PaymentIntent behind " + Prefix(providerTransactionId));
            }

            var charges = await _stripe.ListChargesAsync(
                new ChargeListOptions { PaymentIntent = paymentIntentId, Limit = 10, Expand = ["data.balance_transaction"] }, ct);
            var charge = charges.Data.FirstOrDefault(c => c.Status == "succeeded" && c.BalanceTransaction is not null);
            if (charge?.BalanceTransaction is not { } bt)
            {
                return NotFound(fee, "no succeeded charge with a balance transaction");
            }

            var currency = (bt.Currency ?? string.Empty).ToUpperInvariant();
            fee.Status = PaymentProviderFee.StatusOk;
            fee.ChargeId = charge.Id;
            fee.BalanceTransactionId = bt.Id;
            fee.Currency = currency.Length == 3 ? currency : null;
            fee.Amount = Major(bt.Amount, currency);
            fee.Fee = Major(bt.Fee, currency);
            fee.Net = Major(bt.Net, currency);
            fee.ExchangeRate = bt.ExchangeRate;
            fee.OccurredAt = DateTime.SpecifyKind(bt.Created, DateTimeKind.Utc);
            return fee;
        }
        catch (StripeException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return NotFound(fee, "Stripe does not know " + Prefix(providerTransactionId));
        }
        catch (StripeException ex)
        {
            fee.Status = PaymentProviderFee.StatusError;
            fee.Error = Clip($"Stripe {(int)ex.HttpStatusCode}: {ex.StripeError?.Type ?? "error"}");
            return fee;
        }
    }

    private async Task<string?> PaymentIntentOfAsync(string id, CancellationToken ct)
    {
        if (id.StartsWith(PaymentConstants.StripePrefixes.Session, StringComparison.Ordinal))
        {
            var session = await _stripe.GetCheckoutSessionAsync(id, ct);
            if (session?.PaymentIntentId is { Length: > 0 } fromSession) return fromSession;
            return session?.InvoiceId is { Length: > 0 } invoiceId ? await PaymentIntentOfInvoiceAsync(invoiceId, ct) : null;
        }

        if (id.StartsWith(PaymentConstants.StripePrefixes.PaymentIntent, StringComparison.Ordinal)) return id;
        if (id.StartsWith(PaymentConstants.StripePrefixes.Invoice, StringComparison.Ordinal)) return await PaymentIntentOfInvoiceAsync(id, ct);
        return null;
    }

    private async Task<string?> PaymentIntentOfInvoiceAsync(string invoiceId, CancellationToken ct)
    {
        var payments = await _stripe.ListInvoicePaymentsAsync(new InvoicePaymentListOptions { Invoice = invoiceId, Limit = 10 }, ct);
        return payments.Data
            .Where(p => p.Status == "paid")
            .Select(p => p.Payment?.PaymentIntentId)
            .FirstOrDefault(pi => !string.IsNullOrEmpty(pi));
    }

    /// <summary>Stripe amounts are in the currency's minor unit; VND (and other zero-decimal currencies) have none.</summary>
    public static decimal Major(long minor, string currency)
        => ZeroDecimal.Contains(currency.ToUpperInvariant()) ? minor : minor / 100m;

    /// <summary>Stripe's zero-decimal currencies.</summary>
    private static readonly HashSet<string> ZeroDecimal = new(StringComparer.Ordinal)
    {
        "BIF", "CLP", "DJF", "GNF", "JPY", "KMF", "KRW", "MGA", "PYG", "RWF", "UGX", "VND", "VUV", "XAF", "XOF", "XPF",
    };

    private static PaymentProviderFee NotFound(PaymentProviderFee fee, string reason)
    {
        fee.Status = PaymentProviderFee.StatusNotFound;
        fee.Error = Clip(reason);
        return fee;
    }

    private static string Prefix(string id) => id.Length <= 3 ? id : id[..3] + "…";

    private static string Clip(string value) => value.Length <= 500 ? value : value[..500];
}
