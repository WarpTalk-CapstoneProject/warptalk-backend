using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Stripe;
using Stripe.Checkout;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Services;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Infrastructure.Services;

public class StripePaymentService : IStripePaymentService
{
    private readonly IConfiguration _configuration;
    private readonly IStripeSdkClient _stripeSdkClient;

    public StripePaymentService(IConfiguration configuration, IStripeSdkClient stripeSdkClient)
    {
        _configuration = configuration;
        _stripeSdkClient = stripeSdkClient;
    }

    public async Task<Result<string>> CreateCheckoutSessionAsync(CreateCheckoutSessionRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            bool isPlaceholder = string.IsNullOrEmpty(_configuration[PaymentConstants.StripeConfigKeys.SecretKey]) ||
                                 _configuration[PaymentConstants.StripeConfigKeys.SecretKey] == PaymentConstants.StripePlaceholders.SecretKeyPlaceholder;

            if (isPlaceholder)
            {
                return Result.Failure<string>(
                    PaymentConstants.StripeErrorMessages.SecretKeyNotConfigured,
                    ErrorCodes.InternalServerError);
            }

            var successUrl = _configuration[PaymentConstants.StripeConfigKeys.SuccessUrl];
            var cancelUrl = _configuration[PaymentConstants.StripeConfigKeys.CancelUrl];
            if (string.IsNullOrWhiteSpace(successUrl) || string.IsNullOrWhiteSpace(cancelUrl))
            {
                return Result.Failure<string>(
                    PaymentConstants.StripeErrorMessages.CheckoutUrlsNotConfigured,
                    ErrorCodes.InternalServerError);
            }

            bool isSubscription = request.PaymentType == PaymentConstants.PaymentTypes.Subscription;

            Dictionary<string, string> metadata = new Dictionary<string, string>
            {
                { PaymentConstants.StripeMetadata.UserId, request.UserId.ToString() },
                { PaymentConstants.StripeMetadata.WorkspaceId, request.WorkspaceId.ToString() },
                { PaymentConstants.StripeMetadata.PaymentType, request.PaymentType }
            };

            if (!string.IsNullOrWhiteSpace(request.PlanSlug))
                metadata[PaymentConstants.StripeMetadata.PlanSlug] = request.PlanSlug;

            if (!string.IsNullOrWhiteSpace(request.BillingCycle))
                metadata[PaymentConstants.StripeMetadata.BillingCycle] = request.BillingCycle;

            // WT-429: the credit count rides on the session so both completion paths — the
            // webhook and the return-page read — grant the SAME number without re-deriving it
            // from the amount. Written for top-ups only; the value was already validated and
            // priced server-side by PaymentAppService.
            if (request.Credits > 0)
                metadata[PaymentConstants.StripeMetadata.Credits] =
                    request.Credits.ToString(System.Globalization.CultureInfo.InvariantCulture);

            SessionCreateOptions options = new SessionCreateOptions
            {
                PaymentMethodTypes = new List<string> { PaymentConstants.PaymentMethods.Card },
                LineItems = new List<SessionLineItemOptions>
                {
                    new SessionLineItemOptions
                    {
                        PriceData = new SessionLineItemPriceDataOptions
                        {
                            UnitAmount = string.Equals(request.Currency, PaymentConstants.Currencies.Vnd, StringComparison.OrdinalIgnoreCase)
                                ? (long)request.Amount
                                : (long)(request.Amount * 100),
                            Currency = request.Currency,
                            ProductData = new SessionLineItemPriceDataProductDataOptions
                            {
                                Name = request.PaymentType == PaymentConstants.PaymentTypes.InvoicePayment
                                    ? PaymentConstants.ProductNames.InvoicePayment
                                    : PaymentConstants.ProductNames.SubscriptionPlan,
                            },
                            Recurring = isSubscription ? new SessionLineItemPriceDataRecurringOptions
                            {
                                // The CALLER's billing cycle, not a guess from the price. This
                                // used to infer the interval from the amount — "VND and ≥1,000,000
                                // must be yearly" — which bills the 1,900,000 VND/month Enterprise
                                // plan ANNUALLY at the monthly price. The request already carries
                                // BillingCycle, chosen by the person on the Monthly/Yearly toggle;
                                // inferring it from a number meant the plans page and the
                                // subscription could disagree, silently, about what was bought.
                                //
                                // WT-370: that fix was inert. It compared BillingCycle against
                                // PriceIntervals ("month"/"year"), and the plans page sends
                                // "monthly"/"yearly" — so NEITHER branch ever matched and every
                                // request fell through to the heuristic below. BillingCycleResolver
                                // now owns the comparison and knows both vocabularies.
                                //
                                // The heuristic stays only as the fallback for a request that
                                // names no cycle, so nothing that relied on it starts failing.
                                Interval = BillingCycleResolver.ToPriceInterval(request.BillingCycle)
                                    ?? (string.Equals(request.Currency, PaymentConstants.Currencies.Vnd, StringComparison.OrdinalIgnoreCase)
                                            ? (request.Amount >= 1000000m ? PaymentConstants.PriceIntervals.Year : PaymentConstants.PriceIntervals.Month)
                                            : (request.Amount > 50m ? PaymentConstants.PriceIntervals.Year : PaymentConstants.PriceIntervals.Month))
                            } : null
                        },
                        Quantity = 1,
                    },
                },
                Mode = isSubscription ? PaymentConstants.StripeModes.Subscription : PaymentConstants.StripeModes.Payment,
                SuccessUrl = successUrl,
                CancelUrl = cancelUrl,
                // WT-545: bind the page to the account that started the checkout. Stripe prefills
                // this address and makes the field read-only, so a forwarded link pays as the
                // buyer rather than under a stranger's email — which is what made the resulting
                // customer, receipt and dispute trail name the wrong person. Null when the token
                // carried no email claim; Stripe then asks, exactly as it did before.
                CustomerEmail = string.IsNullOrWhiteSpace(request.BuyerEmail) ? null : request.BuyerEmail,
                Metadata = metadata
            };

            if (isSubscription)
            {
                options.SubscriptionData = new SessionSubscriptionDataOptions { Metadata = metadata };
            }
            else
            {
                options.PaymentIntentData = new SessionPaymentIntentDataOptions { Metadata = metadata };
            }

            Session session = await _stripeSdkClient.CreateCheckoutSessionAsync(options, cancellationToken);

            return Result.Success(session.Url);
        }
        catch (Exception ex)
        {
            return Result.Failure<string>(ex.Message, ErrorCodes.InternalServerError);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// G11. The line is the catalog's: a synced item is sold by its Stripe Price id (so Stripe's
    /// reports name the product), an unsynced one by inline price data for the server-computed
    /// unit amount. A coupon rides as a Stripe discount only when the catalog put one in the
    /// extras — for a one-off pack with an unsynced coupon the discount is already in the unit
    /// amount. One discount per session: Stripe Checkout accepts no more, which is also our
    /// stacking rule.
    /// </remarks>
    public async Task<Result<string>> CreateCatalogCheckoutSessionAsync(
        CreateCheckoutSessionRequest request,
        CheckoutExtras extras,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var configError = CheckoutConfigurationError(out var successUrl, out var cancelUrl);
            if (configError is not null)
            {
                return Result.Failure<string>(configError, ErrorCodes.InternalServerError);
            }

            var metadata = BaseMetadata(request);
            foreach (var (key, value) in extras.Metadata)
            {
                metadata[key] = value;
            }

            SessionLineItemOptions lineItem;
            bool isSubscription;
            if (extras.Line is { } line)
            {
                isSubscription = !string.IsNullOrWhiteSpace(line.RecurringInterval);
                lineItem = !string.IsNullOrWhiteSpace(line.StripePriceId)
                    ? new SessionLineItemOptions { Price = line.StripePriceId, Quantity = line.Quantity }
                    : new SessionLineItemOptions
                    {
                        Quantity = line.Quantity,
                        PriceData = new SessionLineItemPriceDataOptions
                        {
                            UnitAmount = ToMinorUnits(line.UnitAmount, line.Currency),
                            Currency = line.Currency,
                            Product = string.IsNullOrWhiteSpace(line.StripeProductId) ? null : line.StripeProductId,
                            ProductData = string.IsNullOrWhiteSpace(line.StripeProductId)
                                ? new SessionLineItemPriceDataProductDataOptions { Name = line.ProductName }
                                : null,
                            Recurring = isSubscription
                                ? new SessionLineItemPriceDataRecurringOptions { Interval = line.RecurringInterval }
                                : null,
                        },
                    };
            }
            else
            {
                // A plan checkout with a coupon: the plan's own line, unchanged, plus the discount.
                isSubscription = request.PaymentType == PaymentConstants.PaymentTypes.Subscription;
                lineItem = DefaultLineItem(request, isSubscription);
            }

            var options = new SessionCreateOptions
            {
                PaymentMethodTypes = new List<string> { PaymentConstants.PaymentMethods.Card },
                LineItems = new List<SessionLineItemOptions> { lineItem },
                Mode = isSubscription ? PaymentConstants.StripeModes.Subscription : PaymentConstants.StripeModes.Payment,
                SuccessUrl = successUrl,
                CancelUrl = cancelUrl,
                CustomerEmail = string.IsNullOrWhiteSpace(request.BuyerEmail) ? null : request.BuyerEmail,
                Metadata = metadata,
            };

            if (!string.IsNullOrWhiteSpace(extras.StripePromotionCodeId))
            {
                options.Discounts = new List<SessionDiscountOptions> { new() { PromotionCode = extras.StripePromotionCodeId } };
            }
            else if (!string.IsNullOrWhiteSpace(extras.StripeCouponId))
            {
                options.Discounts = new List<SessionDiscountOptions> { new() { Coupon = extras.StripeCouponId } };
            }

            if (isSubscription)
            {
                // The add-on's own subscription carries the metadata, so every later event about it
                // (renewal, update, deletion) is recognisably an add-on's and never the plan's.
                options.SubscriptionData = new SessionSubscriptionDataOptions { Metadata = metadata };
            }
            else
            {
                options.PaymentIntentData = new SessionPaymentIntentDataOptions { Metadata = metadata };
            }

            var session = await _stripeSdkClient.CreateCheckoutSessionAsync(options, cancellationToken);
            return Result.Success(session.Url);
        }
        catch (Exception ex)
        {
            return Result.Failure<string>(ex.Message, ErrorCodes.InternalServerError);
        }
    }

    /// <inheritdoc />
    public async Task<Result<DateTime?>> CancelStripeSubscriptionAtPeriodEndAsync(
        string stripeSubscriptionId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var updated = await _stripeSdkClient.UpdateSubscriptionAsync(
                stripeSubscriptionId,
                new SubscriptionUpdateOptions { CancelAtPeriodEnd = true },
                cancellationToken);
            DateTime? periodEnd = updated.Items?.Data?.FirstOrDefault()?.CurrentPeriodEnd;
            return Result.Success(periodEnd);
        }
        catch (Exception ex)
        {
            return Result.Failure<DateTime?>(ex.Message, ErrorCodes.BillingExternalServiceError);
        }
    }

    private string? CheckoutConfigurationError(out string? successUrl, out string? cancelUrl)
    {
        successUrl = _configuration[PaymentConstants.StripeConfigKeys.SuccessUrl];
        cancelUrl = _configuration[PaymentConstants.StripeConfigKeys.CancelUrl];

        var secretKey = _configuration[PaymentConstants.StripeConfigKeys.SecretKey];
        if (string.IsNullOrEmpty(secretKey) || secretKey == PaymentConstants.StripePlaceholders.SecretKeyPlaceholder)
        {
            return PaymentConstants.StripeErrorMessages.SecretKeyNotConfigured;
        }

        return string.IsNullOrWhiteSpace(successUrl) || string.IsNullOrWhiteSpace(cancelUrl)
            ? PaymentConstants.StripeErrorMessages.CheckoutUrlsNotConfigured
            : null;
    }

    private static Dictionary<string, string> BaseMetadata(CreateCheckoutSessionRequest request)
    {
        var metadata = new Dictionary<string, string>
        {
            { PaymentConstants.StripeMetadata.UserId, request.UserId.ToString() },
            { PaymentConstants.StripeMetadata.WorkspaceId, request.WorkspaceId.ToString() },
            { PaymentConstants.StripeMetadata.PaymentType, request.PaymentType }
        };

        if (!string.IsNullOrWhiteSpace(request.PlanSlug))
            metadata[PaymentConstants.StripeMetadata.PlanSlug] = request.PlanSlug;
        if (!string.IsNullOrWhiteSpace(request.BillingCycle))
            metadata[PaymentConstants.StripeMetadata.BillingCycle] = request.BillingCycle;
        if (request.Credits > 0)
            metadata[PaymentConstants.StripeMetadata.Credits] = request.Credits.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return metadata;
    }

    /// <summary>The plan / invoice / top-up line as <see cref="CreateCheckoutSessionAsync"/> builds it.</summary>
    private static SessionLineItemOptions DefaultLineItem(CreateCheckoutSessionRequest request, bool isSubscription) => new()
    {
        PriceData = new SessionLineItemPriceDataOptions
        {
            UnitAmount = ToMinorUnits(request.Amount, request.Currency),
            Currency = request.Currency,
            ProductData = new SessionLineItemPriceDataProductDataOptions
            {
                Name = request.PaymentType == PaymentConstants.PaymentTypes.InvoicePayment
                    ? PaymentConstants.ProductNames.InvoicePayment
                    : PaymentConstants.ProductNames.SubscriptionPlan,
            },
            Recurring = isSubscription
                ? new SessionLineItemPriceDataRecurringOptions
                {
                    Interval = BillingCycleResolver.ToPriceInterval(request.BillingCycle) ?? PaymentConstants.PriceIntervals.Month,
                }
                : null,
        },
        Quantity = 1,
    };

    /// <summary>Stripe amounts are in the currency's minor unit; VND has none.</summary>
    public static long ToMinorUnits(decimal amount, string currency) =>
        string.Equals(currency, PaymentConstants.Currencies.Vnd, StringComparison.OrdinalIgnoreCase)
            ? (long)decimal.Round(amount, 0, MidpointRounding.AwayFromZero)
            : (long)decimal.Round(amount * 100m, 0, MidpointRounding.AwayFromZero);

    public async Task<Result<bool>> CancelSubscriptionAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        try
        {
            var searchOptions = new SubscriptionSearchOptions
            {
                Query = string.Format(PaymentConstants.StripeSearchQueries.SubscriptionSearchTemplate, PaymentConstants.StripeMetadata.WorkspaceId, workspaceId, PaymentConstants.StripeStatuses.Active)
            };

            var searchResults = await _stripeSdkClient.SearchSubscriptionsAsync(searchOptions, cancellationToken);

            if (searchResults.Data.Count == 0)
                return Result.Success(false);

            foreach (var sub in searchResults.Data)
            {
                var updateOptions = new SubscriptionUpdateOptions
                {
                    CancelAtPeriodEnd = true
                };
                await _stripeSdkClient.UpdateSubscriptionAsync(sub.Id, updateOptions, cancellationToken);
            }

            return Result.Success(true);
        }
        catch (Exception ex)
        {
            return Result.Failure<bool>(ex.Message, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<(string Status, string FailureReason)>> GetPaymentStatusAsync(string providerTransactionId, CancellationToken cancellationToken = default)
    {
        try
        {
            if (providerTransactionId.StartsWith(PaymentConstants.StripePrefixes.Session))
            {
                var session = await _stripeSdkClient.GetCheckoutSessionAsync(providerTransactionId, cancellationToken);
                if (session is null)
                    return Result.Failure<(string, string)>(PaymentConstants.StripeErrorMessages.SessionNotFound, ErrorCodes.NotFound);

                if (session.PaymentStatus == PaymentConstants.StripeStatuses.Paid)
                    return Result.Success((PaymentConstants.PaymentStatuses.Paid, string.Empty));

                if (!string.IsNullOrEmpty(session.PaymentIntentId))
                {
                    var pi = await _stripeSdkClient.GetPaymentIntentAsync(session.PaymentIntentId, cancellationToken);
                    if (pi.Status == PaymentConstants.StripeStatuses.Succeeded) return Result.Success((PaymentConstants.PaymentStatuses.Paid, string.Empty));
                    if (pi.Status == PaymentConstants.StripeStatuses.RequiresPaymentMethod || pi.Status == PaymentConstants.StripeStatuses.Canceled)
                        return Result.Success((PaymentConstants.PaymentStatuses.Failed, pi.LastPaymentError?.Message ?? PaymentConstants.StripePlaceholders.DefaultPaymentFailureOrCanceledReason));
                }

                return Result.Success((PaymentConstants.PaymentStatuses.Pending, string.Empty));
            }
            else if (providerTransactionId.StartsWith(PaymentConstants.StripePrefixes.PaymentIntent))
            {
                var pi = await _stripeSdkClient.GetPaymentIntentAsync(providerTransactionId, cancellationToken);
                if (pi.Status == PaymentConstants.StripeStatuses.Succeeded) return Result.Success((PaymentConstants.PaymentStatuses.Paid, string.Empty));
                if (pi.Status == PaymentConstants.StripeStatuses.RequiresPaymentMethod || pi.Status == PaymentConstants.StripeStatuses.Canceled)
                    return Result.Success((PaymentConstants.PaymentStatuses.Failed, pi.LastPaymentError?.Message ?? PaymentConstants.StripePlaceholders.DefaultPaymentFailureOrCanceledReason));

                return Result.Success((PaymentConstants.PaymentStatuses.Pending, string.Empty));
            }

            return Result.Success((PaymentConstants.StripeStatuses.Unknown, PaymentConstants.StripeErrorMessages.InvalidProviderTxIdFormat));
        }
        catch (Exception ex)
        {
            return Result.Failure<(string, string)>(ex.Message, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<CheckoutSessionDto>> GetCheckoutSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        try
        {
            var session = await _stripeSdkClient.GetCheckoutSessionAsync(sessionId, cancellationToken);
            if (session == null)
                return Result.Failure<CheckoutSessionDto>(PaymentConstants.StripeErrorMessages.SessionNotFound, ErrorCodes.NotFound);

            var metadata = session.Metadata != null
                ? session.Metadata.ToDictionary(k => k.Key, v => v.Value)
                : new Dictionary<string, string>();

            return Result.Success(new CheckoutSessionDto(
                session.Id,
                session.AmountTotal,
                session.Currency,
                metadata,
                session.PaymentStatus,
                session.Status,
                session.PaymentIntentId,
                session.SubscriptionId
            ));
        }
        catch (Exception ex)
        {
            return Result.Failure<CheckoutSessionDto>(ex.Message, ErrorCodes.InternalServerError);
        }
    }
}
