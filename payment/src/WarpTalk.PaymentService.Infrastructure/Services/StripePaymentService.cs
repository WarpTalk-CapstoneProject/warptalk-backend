using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Stripe.Checkout;
using WarpTalk.PaymentService.Application.Interfaces;

namespace WarpTalk.PaymentService.Infrastructure.Services;

public class StripePaymentService : IStripePaymentService
{
    public async Task<string> CreateCheckoutSessionAsync(Guid userId, decimal amount, string currency, string paymentType)
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
            SuccessUrl = "http://localhost:3000/sandbox/workspace-billing?session_id={CHECKOUT_SESSION_ID}",
            CancelUrl = "http://localhost:3000/payment-cancelled",
            PaymentIntentData = new SessionPaymentIntentDataOptions
            {
                Metadata = new Dictionary<string, string>
                {
                    { "UserId", userId.ToString() },
                    { "PaymentType", paymentType }
                }
            },
            Metadata = new Dictionary<string, string>
            {
                { "UserId", userId.ToString() },
                { "PaymentType", paymentType }
            }
        };

        var service = new SessionService();
        Session session = await service.CreateAsync(options);

        return session.Url;
    }
}
