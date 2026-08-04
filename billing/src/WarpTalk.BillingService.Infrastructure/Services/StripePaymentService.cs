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
                                Interval = string.Equals(request.Currency, PaymentConstants.Currencies.Vnd, StringComparison.OrdinalIgnoreCase)
                                    ? (request.Amount >= 1000000m ? PaymentConstants.PriceIntervals.Year : PaymentConstants.PriceIntervals.Month)
                                    : (request.Amount > 50m ? PaymentConstants.PriceIntervals.Year : PaymentConstants.PriceIntervals.Month)
                            } : null
                        },
                        Quantity = 1,
                    },
                },
                Mode = isSubscription ? PaymentConstants.StripeModes.Subscription : PaymentConstants.StripeModes.Payment,
                SuccessUrl = successUrl,
                CancelUrl = cancelUrl,
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
                session.PaymentIntentId
            ));
        }
        catch (Exception ex)
        {
            return Result.Failure<CheckoutSessionDto>(ex.Message, ErrorCodes.InternalServerError);
        }
    }
}
