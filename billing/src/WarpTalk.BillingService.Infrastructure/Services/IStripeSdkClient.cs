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

    // G11 — catalog sync (/admin/packages). Nothing here deletes: Stripe objects are archived.
    Task<Product> GetProductAsync(string productId, CancellationToken cancellationToken = default);
    Task<Product> UpdateProductAsync(string productId, ProductUpdateOptions options, CancellationToken cancellationToken = default);
    Task<Price> GetPriceAsync(string priceId, CancellationToken cancellationToken = default);
    Task<Price> UpdatePriceAsync(string priceId, PriceUpdateOptions options, CancellationToken cancellationToken = default);
    Task<Coupon> CreateCouponAsync(CouponCreateOptions options, CancellationToken cancellationToken = default);
    Task<Coupon> GetCouponAsync(string couponId, CancellationToken cancellationToken = default);
    Task<Coupon> UpdateCouponAsync(string couponId, CouponUpdateOptions options, CancellationToken cancellationToken = default);
    Task<PromotionCode> CreatePromotionCodeAsync(PromotionCodeCreateOptions options, CancellationToken cancellationToken = default);
    Task<PromotionCode> GetPromotionCodeAsync(string promotionCodeId, CancellationToken cancellationToken = default);
    Task<PromotionCode> UpdatePromotionCodeAsync(string promotionCodeId, PromotionCodeUpdateOptions options, CancellationToken cancellationToken = default);
    Task<Stripe.Subscription> GetSubscriptionAsync(string subscriptionId, CancellationToken cancellationToken = default);
}
