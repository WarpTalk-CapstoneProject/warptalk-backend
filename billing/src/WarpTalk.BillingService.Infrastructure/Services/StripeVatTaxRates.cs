using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Stripe;
using WarpTalk.Shared.PlatformSettings;

namespace WarpTalk.BillingService.Infrastructure.Services;

/// <summary>
/// The Stripe TaxRate that adds the platform VAT on top of a checkout line, or null when VAT is off.
/// </summary>
public interface IStripeVatTaxRates
{
    Task<string?> ResolveTaxRateIdAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Owner decision, 3 Oct 2026: every price in the admin is entered WITHOUT VAT, and one
/// platform-wide rate (<see cref="PlatformSettingsCatalog.VatPercent"/>, default 10%) is added on
/// top at Stripe checkout — plans, add-ons and credit top-ups alike.
///
/// WHY A STRIPE TAXRATE AND NOT A SECOND LINE ITEM
///     An exclusive TaxRate on the line is what makes Stripe itself print VAT on the checkout
///     page, the receipt and the invoice, report it as <c>total_details.amount_tax</c>, and carry
///     it onto the subscription a recurring checkout creates, so every renewal is taxed the same
///     way without this service being involved again. A "VAT" line item would be revenue to
///     Stripe and to every report reading line items.
///
/// WHY FIND-OR-CREATE
///     A TaxRate's percentage cannot be edited once created. A change of the setting therefore
///     needs a new rate; an unchanged setting must reuse the old one rather than mint a new
///     object per checkout. Rates this service made carry <see cref="MetadataKey"/>, so a rate
///     somebody created by hand in the dashboard for another purpose is never picked up.
/// </summary>
public sealed class StripeVatTaxRates : IStripeVatTaxRates
{
    public const string MetadataKey = "warptalk_vat";
    public const string DisplayName = "VAT";
    public const string Country = "VN";

    // Per process. Keyed by percentage so a settings change is picked up on the next checkout
    // without a restart, and a stale id is never reused for a different rate.
    private static readonly ConcurrentDictionary<decimal, string> Cache = new();

    private readonly IStripeSdkClient _stripe;
    private readonly IPlatformSettings? _settings;
    private readonly ILogger<StripeVatTaxRates>? _logger;

    public StripeVatTaxRates(
        IStripeSdkClient stripe,
        IPlatformSettings? settings = null,
        ILogger<StripeVatTaxRates>? logger = null)
    {
        _stripe = stripe;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>For tests only: the cache outlives a single instance on purpose.</summary>
    public static void ResetCacheForTests() => Cache.Clear();

    public async Task<string?> ResolveTaxRateIdAsync(CancellationToken cancellationToken = default)
    {
        var percent = await ReadPercentAsync(cancellationToken);
        if (percent <= 0m) return null;

        if (Cache.TryGetValue(percent, out var cached)) return cached;

        var existing = await _stripe.ListTaxRatesAsync(
            new TaxRateListOptions { Active = true, Inclusive = false, Limit = 100 },
            cancellationToken);
        var match = existing?.Data?.FirstOrDefault(rate =>
            rate.Percentage == percent
            && rate.Metadata is not null
            && rate.Metadata.TryGetValue(MetadataKey, out var marker)
            && marker == "1");

        if (match is null)
        {
            match = await _stripe.CreateTaxRateAsync(
                new TaxRateCreateOptions
                {
                    DisplayName = DisplayName,
                    Percentage = percent,
                    Inclusive = false,
                    Country = Country,
                    TaxType = "vat",
                    Description = $"WarpTalk VAT {percent.ToString(CultureInfo.InvariantCulture)}%",
                    Metadata = new Dictionary<string, string> { [MetadataKey] = "1" },
                },
                cancellationToken);
            _logger?.LogInformation(
                "stripe_vat_tax_rate_created: Percent={Percent} TaxRate={TaxRateId}", percent, match.Id);
        }

        Cache[percent] = match.Id;
        return match.Id;
    }

    private async Task<decimal> ReadPercentAsync(CancellationToken cancellationToken)
    {
        // No settings reader (a test, a tool) answers with the catalog default rather than "no
        // VAT": undercharging tax silently is the failure this must not have.
        if (_settings is null) return VatDefaults.Percent;
        return await _settings.GetDecimalAsync(PlatformSettingsCatalog.VatPercent, ct: cancellationToken);
    }
}

/// <summary>The catalog's default, for readers that have no settings service.</summary>
public static class VatDefaults
{
    public const decimal Percent = 10m;
}
