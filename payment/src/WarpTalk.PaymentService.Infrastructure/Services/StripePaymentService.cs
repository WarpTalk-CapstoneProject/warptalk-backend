using System;
using System.Collections.Generic;
using System.Threading.Tasks;
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

    public async Task<string> CreateCheckoutSessionAsync(Guid userId, Guid workspaceId, decimal amount, string currency, string paymentType)
    {
        var options = new SessionCreateOptions
        {
            PaymentMethodTypes = new List<string> { "card" },
            LineItems = new List<SessionLineItemOptions>
            {
                new SessionLineItemOptions
                {
                    PriceData = new SessionLineItemPriceDataOptions
                    {
                        UnitAmount = (long)(amount * 100), // amount in cents
                        Currency = currency,
                        ProductData = new SessionLineItemPriceDataProductDataOptions
                        {
                            Name = paymentType == "CreditTopUp" ? "Credit Top-Up" : "Subscription Plan",
                        },
                    },
                    Quantity = 1,
                },
            },
            Mode = "payment",
            SuccessUrl = _configuration["Stripe:SuccessUrl"] ?? "http://localhost:3000/sandbox/workspace-billing?session_id={CHECKOUT_SESSION_ID}",
            CancelUrl = _configuration["Stripe:CancelUrl"] ?? "http://localhost:3000/payment-cancelled",
            PaymentIntentData = new SessionPaymentIntentDataOptions
            {
                Metadata = new Dictionary<string, string>
                {
                    { "UserId", userId.ToString() },
                    { "WorkspaceId", workspaceId.ToString() },
                    { "PaymentType", paymentType }
                }
            },
            Metadata = new Dictionary<string, string>
            {
                { "UserId", userId.ToString() },
                { "WorkspaceId", workspaceId.ToString() },
                { "PaymentType", paymentType }
            }
        };

        var service = new SessionService();
        Session session = await service.CreateAsync(options);

        return session.Url;
    }
}
