using Stripe;
using Stripe.Checkout;

namespace WarpTalk.BillingService.Infrastructure.Services;

public interface IStripeSdkClient
{
    Task<Session> CreateCheckoutSessionAsync(SessionCreateOptions options, CancellationToken cancellationToken = default);
    Task<Session?> GetCheckoutSessionAsync(string sessionId, CancellationToken cancellationToken = default);
    Task<StripeSearchResult<Stripe.Subscription>> SearchSubscriptionsAsync(SubscriptionSearchOptions options, CancellationToken cancellationToken = default);
    Task<Stripe.Subscription> UpdateSubscriptionAsync(string subscriptionId, SubscriptionUpdateOptions options, CancellationToken cancellationToken = default);
    Task<StripeList<Product>> ListProductsAsync(ProductListOptions options, CancellationToken cancellationToken = default);
    Task<Product> CreateProductAsync(ProductCreateOptions options, CancellationToken cancellationToken = default);
    Task<Price> CreatePriceAsync(PriceCreateOptions options, CancellationToken cancellationToken = default);
    Task<PaymentIntent> GetPaymentIntentAsync(string paymentIntentId, CancellationToken cancellationToken = default);
    Task<Invoice> GetInvoiceAsync(string invoiceId, CancellationToken cancellationToken = default);
    Task<Refund> CreateRefundAsync(RefundCreateOptions options, CancellationToken cancellationToken = default);
}
