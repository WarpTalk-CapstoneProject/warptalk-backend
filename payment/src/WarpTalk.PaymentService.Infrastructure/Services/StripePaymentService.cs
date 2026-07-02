using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Stripe;
using Stripe.Checkout;
using WarpTalk.PaymentService.Application.Interfaces;

using Microsoft.Extensions.Configuration;

namespace WarpTalk.PaymentService.Infrastructure.Services;

public class StripePaymentService : IStripePaymentService
{
    private readonly IConfiguration _configuration;

    public StripePaymentService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public async Task<string> CreateCheckoutSessionAsync(Guid userId, Guid workspaceId, decimal amount, string currency, string paymentType, string planSlug = "", string billingCycle = "")
    {
        bool isSubscription = paymentType == "Subscription";

        var metadata = new Dictionary<string, string>
        {
            { "UserId", userId.ToString() },
            { "WorkspaceId", workspaceId.ToString() },
            { "PaymentType", paymentType }
        };

        if (!string.IsNullOrWhiteSpace(planSlug))
        {
            metadata["PlanSlug"] = planSlug;
        }

        if (!string.IsNullOrWhiteSpace(billingCycle))
        {
            metadata["BillingCycle"] = billingCycle;
        }

        var options = new SessionCreateOptions
        {
            PaymentMethodTypes = new List<string> { "card" },
            LineItems = new List<SessionLineItemOptions>
            {
                new SessionLineItemOptions
                {
                    PriceData = new SessionLineItemPriceDataOptions
                    {
                        UnitAmount = string.Equals(currency, "vnd", StringComparison.OrdinalIgnoreCase)
                            ? (long)amount
                            : (long)(amount * 100), // amount in cents
                        Currency = currency,
                        ProductData = new SessionLineItemPriceDataProductDataOptions
                        {
                            Name = paymentType == "CreditTopUp" ? "Credit Top-Up" : "Subscription Plan",
                        },
                        Recurring = isSubscription ? new SessionLineItemPriceDataRecurringOptions
                        {
                            Interval = string.Equals(currency, "vnd", StringComparison.OrdinalIgnoreCase)
                                ? (amount >= 1000000m ? "year" : "month")
                                : (amount > 50m ? "year" : "month")
                        } : null
                    },
                    Quantity = 1,
                },
            },
            Mode = isSubscription ? "subscription" : "payment",
            SuccessUrl = _configuration["Stripe:SuccessUrl"] ?? "http://localhost:3000/sandbox/workspace-billing?session_id={CHECKOUT_SESSION_ID}",
            CancelUrl = _configuration["Stripe:CancelUrl"] ?? "http://localhost:3000/payment-cancelled",
            Metadata = metadata
        };

        if (isSubscription)
        {
            options.SubscriptionData = new SessionSubscriptionDataOptions
            {
                Metadata = metadata
            };
        }
        else
        {
            options.PaymentIntentData = new SessionPaymentIntentDataOptions
            {
                Metadata = metadata
            };
        }

        var service = new SessionService();
        Session session = await service.CreateAsync(options);

        return session.Url;
    }

    public async Task<bool> UpdateSubscriptionAsync(Guid workspaceId, decimal newAmount, string currency, string newPlanName)
    {
        var service = new SubscriptionService();
        var searchOptions = new SubscriptionSearchOptions
        {
            Query = $"metadata['WorkspaceId']:'{workspaceId}' AND status:'active'"
        };

        var searchResults = await service.SearchAsync(searchOptions);
        
        if (searchResults.Data.Count == 0)
        {
            return false;
        }

        var subscription = searchResults.Data.First();
        var subscriptionItemId = subscription.Items.Data[0].Id;

        var options = new SubscriptionUpdateOptions
        {
            Items = new List<SubscriptionItemOptions>
            {
                new SubscriptionItemOptions
                {
                    Id = subscriptionItemId,
                    PriceData = new SubscriptionItemPriceDataOptions
                    {
                        UnitAmount = string.Equals(currency, "vnd", StringComparison.OrdinalIgnoreCase)
                            ? (long)newAmount
                            : (long)(newAmount * 100),
                        Currency = currency,
                        ProductData = new SubscriptionItemPriceDataProductDataOptions
                        {
                            Name = $"{newPlanName} Subscription"
                        },
                        Recurring = new SubscriptionItemPriceDataRecurringOptions
                        {
                            Interval = "month"
                        }
                    }
                }
            },
            ProrationBehavior = "always_invoice"
        };

        await service.UpdateAsync(subscription.Id, options);
        return true;
    }

    public async Task<bool> CancelSubscriptionAsync(Guid workspaceId)
    {
        var service = new SubscriptionService();
        var searchOptions = new SubscriptionSearchOptions
        {
            Query = $"metadata['WorkspaceId']:'{workspaceId}' AND status:'active'"
        };

        var searchResults = await service.SearchAsync(searchOptions);
        
        if (searchResults.Data.Count == 0)
        {
            return false;
        }

        foreach (var sub in searchResults.Data)
        {
            var updateOptions = new SubscriptionUpdateOptions
            {
                CancelAtPeriodEnd = true
            };
            await service.UpdateAsync(sub.Id, updateOptions);
        }

        return true;
    }

    public async Task<(string Status, string FailureReason)> GetPaymentStatusAsync(string providerTransactionId)
    {
        // ProviderTransactionId is usually either SessionId or PaymentIntentId
        try
        {
            if (providerTransactionId.StartsWith("cs_"))
            {
                var sessionService = new SessionService();
                var session = await sessionService.GetAsync(providerTransactionId);
                
                if (session.PaymentStatus == "paid")
                {
                    return ("paid", string.Empty);
                }
                
                // If there's a payment intent attached, check its status
                if (!string.IsNullOrEmpty(session.PaymentIntentId))
                {
                    var piService = new PaymentIntentService();
                    var pi = await piService.GetAsync(session.PaymentIntentId);
                    if (pi.Status == "succeeded") return ("paid", string.Empty);
                    if (pi.Status == "requires_payment_method" || pi.Status == "canceled") return ("failed", pi.LastPaymentError?.Message ?? "Payment failed or canceled");
                }

                return ("pending", string.Empty);
            }
            else if (providerTransactionId.StartsWith("pi_"))
            {
                var piService = new PaymentIntentService();
                var pi = await piService.GetAsync(providerTransactionId);
                if (pi.Status == "succeeded") return ("paid", string.Empty);
                if (pi.Status == "requires_payment_method" || pi.Status == "canceled") return ("failed", pi.LastPaymentError?.Message ?? "Payment failed or canceled");
                
                return ("pending", string.Empty);
            }
            
            return ("unknown", "Invalid provider transaction ID format");
        }
        catch (Exception ex)
        {
            return ("error", ex.Message);
        }
    }

    public async Task<bool> RefundPaymentAsync(string providerTransactionId)
    {
        try
        {
            string paymentIntentId = providerTransactionId;
            
            // If it's a session ID, get the payment intent ID
            if (providerTransactionId.StartsWith("cs_"))
            {
                var sessionService = new SessionService();
                var session = await sessionService.GetAsync(providerTransactionId);
                paymentIntentId = session.PaymentIntentId;
                
                if (string.IsNullOrEmpty(paymentIntentId))
                {
                    // Maybe it's an invoice
                    if (!string.IsNullOrEmpty(session.InvoiceId))
                    {
                        var invoiceService = new InvoiceService();
                        var invoice = await invoiceService.GetAsync(session.InvoiceId);
                        paymentIntentId = ((dynamic)invoice).PaymentIntentId;
                    }
                }
            }

            if (string.IsNullOrEmpty(paymentIntentId))
            {
                return false;
            }

            var refundService = new RefundService();
            var options = new RefundCreateOptions
            {
                PaymentIntent = paymentIntentId
            };
            
            await refundService.CreateAsync(options);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
