using Stripe;
using Stripe.Checkout;

namespace WarpTalk.BillingService.Infrastructure.Services;

public sealed class StripeSdkClient : IStripeSdkClient
{
    private readonly SessionService _sessionService = new();
    private readonly Stripe.SubscriptionService _subscriptionService = new();
    private readonly ProductService _productService = new();
    private readonly PriceService _priceService = new();
    private readonly PaymentIntentService _paymentIntentService = new();
    private readonly Stripe.InvoiceService _invoiceService = new();

    public Task<Session> CreateCheckoutSessionAsync(SessionCreateOptions options, CancellationToken cancellationToken = default)
        => _sessionService.CreateAsync(options, cancellationToken: cancellationToken);

    public Task<Session?> GetCheckoutSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        => _sessionService.GetAsync(sessionId, cancellationToken: cancellationToken)!;

    public Task<StripeSearchResult<Stripe.Subscription>> SearchSubscriptionsAsync(SubscriptionSearchOptions options, CancellationToken cancellationToken = default)
        => _subscriptionService.SearchAsync(options, cancellationToken: cancellationToken);

    public Task<Stripe.Subscription> UpdateSubscriptionAsync(string subscriptionId, SubscriptionUpdateOptions options, CancellationToken cancellationToken = default)
        => _subscriptionService.UpdateAsync(subscriptionId, options, cancellationToken: cancellationToken);

    public Task<StripeList<Product>> ListProductsAsync(ProductListOptions options, CancellationToken cancellationToken = default)
        => _productService.ListAsync(options, cancellationToken: cancellationToken);

    public Task<Product> CreateProductAsync(ProductCreateOptions options, CancellationToken cancellationToken = default)
        => _productService.CreateAsync(options, cancellationToken: cancellationToken);

    public Task<Price> CreatePriceAsync(PriceCreateOptions options, CancellationToken cancellationToken = default)
        => _priceService.CreateAsync(options, cancellationToken: cancellationToken);

    public Task<PaymentIntent> GetPaymentIntentAsync(string paymentIntentId, CancellationToken cancellationToken = default)
        => _paymentIntentService.GetAsync(paymentIntentId, cancellationToken: cancellationToken);

    public Task<Invoice> GetInvoiceAsync(string invoiceId, CancellationToken cancellationToken = default)
        => _invoiceService.GetAsync(invoiceId, cancellationToken: cancellationToken);
}
