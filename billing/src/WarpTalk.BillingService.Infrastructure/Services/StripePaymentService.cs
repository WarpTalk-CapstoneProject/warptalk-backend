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
                var payload = new
                {
                    UserId = request.UserId,
                    WorkspaceId = request.WorkspaceId,
                    Amount = request.Amount,
                    Currency = request.Currency,
                    PaymentType = request.PaymentType,
                    PlanSlug = request.PlanSlug,
                    BillingCycle = request.BillingCycle
                };

                string payloadJson = System.Text.Json.JsonSerializer.Serialize(payload);
                string payloadBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payloadJson));
                string successUrl = _configuration[PaymentConstants.StripeConfigKeys.SuccessUrl] ?? PaymentConstants.StripeDefaultUrls.MockSuccessUrl;
                return Result.Success(successUrl.Replace(PaymentConstants.StripePlaceholders.UnknownWebhookSessionUrlToken, PaymentConstants.StripePrefixes.MockSession + payloadBase64));
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
                                Name = request.PaymentType == PaymentConstants.PaymentTypes.CreditTopUp ? PaymentConstants.ProductNames.CreditTopUp : PaymentConstants.ProductNames.SubscriptionPlan,
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
                SuccessUrl = _configuration[PaymentConstants.StripeConfigKeys.SuccessUrl] ?? PaymentConstants.StripeDefaultUrls.SandboxSuccessUrl,
                CancelUrl = _configuration[PaymentConstants.StripeConfigKeys.CancelUrl] ?? PaymentConstants.StripeDefaultUrls.CancelUrl,
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

    public async Task<Result<bool>> UpdateSubscriptionAsync(UpdateStripeSubscriptionRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var searchOptions = new SubscriptionSearchOptions
            {
                Query = string.Format(PaymentConstants.StripeSearchQueries.SubscriptionSearchTemplate, PaymentConstants.StripeMetadata.WorkspaceId, request.WorkspaceId, PaymentConstants.StripeStatuses.Active)
            };

            var searchResults = await _stripeSdkClient.SearchSubscriptionsAsync(searchOptions, cancellationToken);

            if (searchResults.Data.Count == 0)
                return Result.Success(false);

            var subscription = searchResults.Data.First();
            var subscriptionItemId = subscription.Items.Data[0].Id;

            var productList = await _stripeSdkClient.ListProductsAsync(new ProductListOptions { Active = true, Limit = 100 }, cancellationToken);
            var existingProduct = productList.Data.FirstOrDefault(p => p.Name == request.PlanSlug);

            string productId;
            if (existingProduct != null)
            {
                productId = existingProduct.Id;
            }
            else
            {
                var newProduct = await _stripeSdkClient.CreateProductAsync(new ProductCreateOptions
                {
                    Name = request.PlanSlug,
                    Metadata = new Dictionary<string, string>
                    {
                        { PaymentConstants.StripeMetadata.PlanSlug, request.PlanSlug.ToLowerInvariant() }
                    }
                }, cancellationToken);
                productId = newProduct.Id;
            }

            var newPrice = await _stripeSdkClient.CreatePriceAsync(new PriceCreateOptions
            {
                Product = productId,
                UnitAmount = string.Equals(request.Currency, PaymentConstants.Currencies.Vnd, StringComparison.OrdinalIgnoreCase)
                    ? (long)request.NewAmount
                    : (long)(request.NewAmount * 100),
                Currency = request.Currency,
                Recurring = new PriceRecurringOptions
                {
                    Interval = PaymentConstants.PriceIntervals.Month
                }
            }, cancellationToken);

            var options = new SubscriptionUpdateOptions
            {
                Items = new List<SubscriptionItemOptions>
                {
                    new SubscriptionItemOptions
                    {
                        Id = subscriptionItemId,
                        Price = newPrice.Id
                    }
                },
                Metadata = new Dictionary<string, string>
                {
                    { PaymentConstants.StripeMetadata.PlanSlug, request.PlanSlug.ToLowerInvariant() }
                },
                ProrationBehavior = PaymentConstants.StripeProrationBehaviors.AlwaysInvoice
            };

            await _stripeSdkClient.UpdateSubscriptionAsync(subscription.Id, options, cancellationToken);
            return Result.Success(true);
        }
        catch (Exception ex)
        {
            return Result.Failure<bool>(ex.Message, ErrorCodes.InternalServerError);
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

    public async Task<Result<bool>> RefundPaymentAsync(string providerTransactionId, CancellationToken cancellationToken = default)
    {
        try
        {
            string paymentIntentId = providerTransactionId;

            if (providerTransactionId.StartsWith(PaymentConstants.StripePrefixes.Session))
            {
                var session = await _stripeSdkClient.GetCheckoutSessionAsync(providerTransactionId, cancellationToken);
                if (session is null)
                    return Result.Success(false);

                paymentIntentId = session.PaymentIntentId;

                if (string.IsNullOrEmpty(paymentIntentId))
                {
                    if (!string.IsNullOrEmpty(session.InvoiceId))
                    {
                        var invoice = await _stripeSdkClient.GetInvoiceAsync(session.InvoiceId, cancellationToken);
                        paymentIntentId = ((dynamic)invoice).PaymentIntentId;
                    }
                }
            }

            if (string.IsNullOrEmpty(paymentIntentId))
                return Result.Success(false);

            var options = new RefundCreateOptions
            {
                PaymentIntent = paymentIntentId
            };

            await _stripeSdkClient.CreateRefundAsync(options, cancellationToken);
            return Result.Success(true);
        }
        catch (Exception ex)
        {
            return Result.Failure<bool>(ex.Message, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<CheckoutSessionDto>> GetCheckoutSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        try
        {
            if (sessionId.StartsWith(PaymentConstants.StripePrefixes.MockSession))
            {
                var payloadBase64 = sessionId.Substring(PaymentConstants.StripePrefixes.MockSession.Length);
                var payloadJson = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payloadBase64));
                var payload = System.Text.Json.JsonSerializer.Deserialize<MockSessionPayload>(payloadJson, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (payload == null)
                    return Result.Failure<CheckoutSessionDto>(PaymentConstants.StripeErrorMessages.InvalidMockSessionPayload, ErrorCodes.ValidationError);

                var metadata = new Dictionary<string, string>
                {
                    { PaymentConstants.StripeMetadata.UserId, payload.UserId.ToString() },
                    { PaymentConstants.StripeMetadata.WorkspaceId, payload.WorkspaceId.ToString() },
                    { PaymentConstants.StripeMetadata.PaymentType, payload.PaymentType },
                    { PaymentConstants.StripeMetadata.PlanSlug, payload.PlanSlug ?? "" },
                    { PaymentConstants.StripeMetadata.BillingCycle, payload.BillingCycle ?? "" }
                };

                return Result.Success(new CheckoutSessionDto(
                    sessionId,
                    (long)(string.Equals(payload.Currency, PaymentConstants.Currencies.Vnd, StringComparison.OrdinalIgnoreCase) ? payload.Amount : payload.Amount * 100),
                    payload.Currency,
                    metadata,
                    PaymentConstants.StripeStatuses.Paid,
                    PaymentConstants.StripeStatuses.Complete,
                    PaymentConstants.StripePrefixes.MockPaymentIntent + Guid.NewGuid().ToString("N")
                ));
            }
            else
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
        }
        catch (Exception ex)
        {
            return Result.Failure<CheckoutSessionDto>(ex.Message, ErrorCodes.InternalServerError);
        }
    }

    private class MockSessionPayload
    {
        public Guid UserId { get; set; }
        public Guid WorkspaceId { get; set; }
        public decimal Amount { get; set; }
        public string Currency { get; set; } = PaymentConstants.Currencies.Usd;
        public string PaymentType { get; set; } = string.Empty;
        public string PlanSlug { get; set; } = string.Empty;
        public string BillingCycle { get; set; } = string.Empty;
    }
}
