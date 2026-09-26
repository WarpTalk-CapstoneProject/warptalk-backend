using System.Globalization;
using Microsoft.Extensions.Configuration;
using Stripe;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Services;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Infrastructure.Services;

/// <summary>
/// #466 — recurring plan billing over the Stripe SDK. Follows the G11 catalog-sync rules
/// (<see cref="StripeCatalogSyncService"/>): Stripe objects are never deleted, a price that no longer
/// matches is archived and replaced, and every error string is redacted before it leaves.
/// </summary>
public sealed class StripeRecurringGateway : IStripeRecurringGateway
{
    private readonly IStripeSdkClient _sdk;
    private readonly IConfiguration _configuration;

    public StripeRecurringGateway(IStripeSdkClient sdk, IConfiguration configuration)
    {
        _sdk = sdk;
        _configuration = configuration;
    }

    public bool IsConfigured
    {
        get
        {
            var key = _configuration[PaymentConstants.StripeConfigKeys.SecretKey];
            return !string.IsNullOrWhiteSpace(key) && key != PaymentConstants.StripePlaceholders.SecretKeyPlaceholder;
        }
    }

    public async Task<Result<string>> EnsurePlanPriceAsync(
        Domain.Entities.Plan plan,
        string billingCycle,
        decimal amount,
        string currency,
        CancellationToken ct = default)
    {
        try
        {
            var interval = BillingCycleResolver.ToPriceInterval(PlanPricing.NormalizeCycle(billingCycle))!;
            var key = PlanPricing.PriceKey(billingCycle, currency);
            var unitAmount = StripePaymentService.ToMinorUnits(amount, currency);
            var metadata = new Dictionary<string, string>
            {
                [PackageCatalogConstants.StripeMetadata.CatalogItemType] = "plan",
                [PackageCatalogConstants.StripeMetadata.CatalogItemId] = plan.Id.ToString(),
                ["plan_slug"] = plan.Slug,
            };

            plan.StripeProductId = await EnsureProductAsync(plan, metadata, ct);

            var ids = new Dictionary<string, string>(PackageCatalogRules.ReadPriceIds(plan.StripePriceIds));
            if (ids.TryGetValue(key, out var storedId) && !string.IsNullOrWhiteSpace(storedId))
            {
                var stored = await _sdk.GetPriceAsync(storedId, ct);
                if (Matches(stored, plan.StripeProductId, unitAmount, currency, interval) && stored.Active)
                {
                    return Result.Success(stored.Id);
                }

                // The plan's price changed: the old Price is archived, never deleted. Subscriptions
                // already on it keep being charged what they were sold at.
                if (stored.Active)
                {
                    await _sdk.UpdatePriceAsync(stored.Id, new PriceUpdateOptions { Active = false }, ct);
                }

                ids.Remove(key);
            }

            // Two checkouts racing on a plan with no price yet must not mint two prices: the lookup
            // key names exactly one (plan, cycle, currency, amount), so the loser finds the winner's.
            var lookupKey = LookupKey(plan, key, unitAmount);
            var found = await _sdk.ListPricesAsync(new PriceListOptions { LookupKeys = new List<string> { lookupKey }, Limit = 1 }, ct);
            var existing = found?.Data?.FirstOrDefault(p => Matches(p, plan.StripeProductId, unitAmount, currency, interval));
            if (existing is not null)
            {
                if (!existing.Active)
                {
                    await _sdk.UpdatePriceAsync(existing.Id, new PriceUpdateOptions { Active = true }, ct);
                }

                ids[key] = existing.Id;
            }
            else
            {
                var created = await _sdk.CreatePriceAsync(new PriceCreateOptions
                {
                    Product = plan.StripeProductId,
                    Currency = currency,
                    UnitAmount = unitAmount,
                    Recurring = new PriceRecurringOptions { Interval = interval },
                    LookupKey = lookupKey,
                    Nickname = $"{plan.Slug} {key}",
                    Metadata = metadata,
                }, ct);
                ids[key] = created.Id;
            }

            plan.StripePriceIds = PackageCatalogRules.WritePriceIds(ids);
            return Result.Success(ids[key]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result.Failure<string>(StripeCatalogSyncService.Redact(ex), ErrorCodes.BillingExternalServiceError);
        }
    }

    public async Task<Result<StripeRecurringSnapshot>> GetSubscriptionAsync(string stripeSubscriptionId, CancellationToken ct = default)
    {
        try
        {
            var subscription = await _sdk.GetSubscriptionWithPaymentMethodAsync(stripeSubscriptionId, ct);
            return Result.Success(await ToSnapshotAsync(subscription, ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result.Failure<StripeRecurringSnapshot>(StripeCatalogSyncService.Redact(ex), ErrorCodes.BillingExternalServiceError);
        }
    }

    public async Task<Result<StripeRecurringSnapshot>> SetCancelAtPeriodEndAsync(string stripeSubscriptionId, bool cancelAtPeriodEnd, CancellationToken ct = default)
    {
        try
        {
            await _sdk.UpdateSubscriptionAsync(
                stripeSubscriptionId,
                new SubscriptionUpdateOptions { CancelAtPeriodEnd = cancelAtPeriodEnd },
                ct);
            var subscription = await _sdk.GetSubscriptionWithPaymentMethodAsync(stripeSubscriptionId, ct);
            return Result.Success(await ToSnapshotAsync(subscription, ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result.Failure<StripeRecurringSnapshot>(StripeCatalogSyncService.Redact(ex), ErrorCodes.BillingExternalServiceError);
        }
    }

    public async Task<Result> CancelNowAsync(string stripeSubscriptionId, CancellationToken ct = default)
    {
        try
        {
            await _sdk.CancelSubscriptionAsync(
                stripeSubscriptionId,
                new SubscriptionCancelOptions { InvoiceNow = false, Prorate = false },
                ct);
            return Result.Success();
        }
        catch (StripeException ex) when (ex.StripeError?.Code == "resource_missing"
                                         || (ex.StripeError?.Message?.Contains("canceled", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            // Already gone or already canceled: the outcome the caller wanted.
            return Result.Success();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result.Failure(StripeCatalogSyncService.Redact(ex), ErrorCodes.BillingExternalServiceError);
        }
    }

    public async Task<Result<string>> CreateBillingPortalUrlAsync(string stripeCustomerId, string? returnPath, CancellationToken ct = default)
    {
        var returnUrl = ResolveReturnUrl(returnPath);
        if (returnUrl is null)
        {
            return Result.Failure<string>(PaymentConstants.StripeErrorMessages.CheckoutUrlsNotConfigured, ErrorCodes.InternalServerError);
        }

        try
        {
            var session = await _sdk.CreateBillingPortalSessionAsync(
                new Stripe.BillingPortal.SessionCreateOptions { Customer = stripeCustomerId, ReturnUrl = returnUrl },
                ct);
            return Result.Success(session.Url);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result.Failure<string>(StripeCatalogSyncService.Redact(ex), ErrorCodes.BillingExternalServiceError);
        }
    }

    /// <summary>The configured checkout origin plus a same-site path; anything else becomes "/".</summary>
    public string? ResolveReturnUrl(string? returnPath)
    {
        var configured = _configuration[PaymentConstants.StripeConfigKeys.SuccessUrl];
        if (string.IsNullOrWhiteSpace(configured) || !Uri.TryCreate(configured, UriKind.Absolute, out var origin))
        {
            return null;
        }

        var path = string.IsNullOrWhiteSpace(returnPath)
                   || !returnPath.StartsWith('/')
                   || returnPath.StartsWith("//", StringComparison.Ordinal)
                   || returnPath.Contains('\\')
            ? "/"
            : returnPath;
        return new Uri(new Uri(origin.GetLeftPart(UriPartial.Authority)), path).ToString();
    }

    private async Task<string> EnsureProductAsync(Domain.Entities.Plan plan, Dictionary<string, string> metadata, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(plan.StripeProductId))
        {
            return plan.StripeProductId;
        }

        var product = await _sdk.CreateProductAsync(new ProductCreateOptions
        {
            Name = plan.Name.Length > 250 ? plan.Name[..250] : plan.Name,
            Active = true,
            Metadata = metadata,
        }, ct);
        return product.Id;
    }

    private async Task<StripeRecurringSnapshot> ToSnapshotAsync(Stripe.Subscription subscription, CancellationToken ct)
    {
        var item = subscription.Items?.Data?.FirstOrDefault();
        var card = subscription.DefaultPaymentMethod?.Card;

        decimal? nextAmount = null;
        string? nextCurrency = null;
        DateTime? nextAt = null;
        var renewing = !subscription.CancelAtPeriodEnd
                       && subscription.Status is SubscriptionConstants.StripeSubscriptionStatuses.Active
                           or SubscriptionConstants.StripeSubscriptionStatuses.Trialing
                           or SubscriptionConstants.StripeSubscriptionStatuses.PastDue;
        if (renewing)
        {
            nextAt = item?.CurrentPeriodEnd;
            try
            {
                // The upcoming invoice, so a coupon that repeats is reflected in what the page promises.
                var preview = await _sdk.CreateInvoicePreviewAsync(new InvoiceCreatePreviewOptions { Subscription = subscription.Id }, ct);
                nextAmount = FromMinor(preview.AmountDue, preview.Currency);
                nextCurrency = preview.Currency;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (item?.Price?.UnitAmount is { } unit)
                {
                    nextAmount = FromMinor(unit * (item.Quantity > 0 ? item.Quantity : 1), item.Price.Currency);
                    nextCurrency = item.Price.Currency;
                }
            }
        }

        return new StripeRecurringSnapshot(
            subscription.Id,
            subscription.Status,
            subscription.CancelAtPeriodEnd,
            item?.CurrentPeriodEnd,
            subscription.CustomerId,
            nextAmount,
            nextCurrency,
            nextAt,
            card?.Brand,
            card?.Last4,
            card is null ? null : (int)card.ExpMonth,
            card is null ? null : (int)card.ExpYear);
    }

    private static bool Matches(Price price, string productId, long unitAmount, string currency, string interval) =>
        string.Equals(price.ProductId, productId, StringComparison.Ordinal)
        && string.Equals(price.Currency, currency, StringComparison.OrdinalIgnoreCase)
        && price.UnitAmount == unitAmount
        && string.Equals(price.Recurring?.Interval, interval, StringComparison.Ordinal);

    private static string LookupKey(Domain.Entities.Plan plan, string key, long unitAmount) =>
        string.Create(CultureInfo.InvariantCulture, $"warptalk_plan_{plan.Id:N}_{key}_{unitAmount}");

    private static decimal FromMinor(long amount, string? currency) =>
        string.Equals(currency, PaymentConstants.Currencies.Vnd, StringComparison.OrdinalIgnoreCase) ? amount : amount / 100m;
}
