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
    private readonly CouponService _couponService = new();
    private readonly PromotionCodeService _promotionCodeService = new();
    private readonly ChargeService _chargeService = new();
    private readonly InvoicePaymentService _invoicePaymentService = new();
    private readonly Stripe.BillingPortal.SessionService _billingPortalSessionService = new();

    public Task<StripeList<Price>> ListPricesAsync(PriceListOptions options, CancellationToken cancellationToken = default)
        => _priceService.ListAsync(options, cancellationToken: cancellationToken);

    public Task<Stripe.Subscription> GetSubscriptionWithPaymentMethodAsync(string subscriptionId, CancellationToken cancellationToken = default)
        => _subscriptionService.GetAsync(
            subscriptionId,
            new SubscriptionGetOptions { Expand = new List<string> { "default_payment_method" } },
            cancellationToken: cancellationToken);

    public Task<Stripe.Subscription> CancelSubscriptionAsync(string subscriptionId, SubscriptionCancelOptions options, CancellationToken cancellationToken = default)
        => _subscriptionService.CancelAsync(subscriptionId, options, cancellationToken: cancellationToken);

    public Task<Invoice> CreateInvoicePreviewAsync(InvoiceCreatePreviewOptions options, CancellationToken cancellationToken = default)
        => _invoiceService.CreatePreviewAsync(options, cancellationToken: cancellationToken);

    public Task<Stripe.BillingPortal.Session> CreateBillingPortalSessionAsync(Stripe.BillingPortal.SessionCreateOptions options, CancellationToken cancellationToken = default)
        => _billingPortalSessionService.CreateAsync(options, cancellationToken: cancellationToken);

    public Task<StripeList<Charge>> ListChargesAsync(ChargeListOptions options, CancellationToken cancellationToken = default)
        => _chargeService.ListAsync(options, cancellationToken: cancellationToken);

    public Task<StripeList<InvoicePayment>> ListInvoicePaymentsAsync(InvoicePaymentListOptions options, CancellationToken cancellationToken = default)
        => _invoicePaymentService.ListAsync(options, cancellationToken: cancellationToken);

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

    public Task<Product> GetProductAsync(string productId, CancellationToken cancellationToken = default)
        => _productService.GetAsync(productId, cancellationToken: cancellationToken);

    public Task<Product> UpdateProductAsync(string productId, ProductUpdateOptions options, CancellationToken cancellationToken = default)
        => _productService.UpdateAsync(productId, options, cancellationToken: cancellationToken);

    public Task<Price> GetPriceAsync(string priceId, CancellationToken cancellationToken = default)
        => _priceService.GetAsync(priceId, cancellationToken: cancellationToken);

    public Task<Price> UpdatePriceAsync(string priceId, PriceUpdateOptions options, CancellationToken cancellationToken = default)
        => _priceService.UpdateAsync(priceId, options, cancellationToken: cancellationToken);

    public Task<Coupon> CreateCouponAsync(CouponCreateOptions options, CancellationToken cancellationToken = default)
        => _couponService.CreateAsync(options, cancellationToken: cancellationToken);

    public Task<Coupon> GetCouponAsync(string couponId, CancellationToken cancellationToken = default)
        => _couponService.GetAsync(couponId, cancellationToken: cancellationToken);

    public Task<Coupon> UpdateCouponAsync(string couponId, CouponUpdateOptions options, CancellationToken cancellationToken = default)
        => _couponService.UpdateAsync(couponId, options, cancellationToken: cancellationToken);

    public Task<PromotionCode> CreatePromotionCodeAsync(PromotionCodeCreateOptions options, CancellationToken cancellationToken = default)
        => _promotionCodeService.CreateAsync(options, cancellationToken: cancellationToken);

    public Task<PromotionCode> GetPromotionCodeAsync(string promotionCodeId, CancellationToken cancellationToken = default)
        => _promotionCodeService.GetAsync(promotionCodeId, cancellationToken: cancellationToken);

    public Task<PromotionCode> UpdatePromotionCodeAsync(string promotionCodeId, PromotionCodeUpdateOptions options, CancellationToken cancellationToken = default)
        => _promotionCodeService.UpdateAsync(promotionCodeId, options, cancellationToken: cancellationToken);

    public Task<Stripe.Subscription> GetSubscriptionAsync(string subscriptionId, CancellationToken cancellationToken = default)
        => _subscriptionService.GetAsync(subscriptionId, cancellationToken: cancellationToken);
}
