using System.Globalization;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Domain.Constants;

namespace WarpTalk.BillingService.Application.Mappers;

/// <summary>
/// G11 — reads the catalog fields a checkout wrote on a Stripe session (or subscription) back into
/// the payment event. Both completion paths — the webhook and the return page — go through this,
/// so they cannot disagree about which pack, coupon or quantity a payment was for.
/// </summary>
public static class CatalogMetadataMapper
{
    public static StripePaymentEventRequest WithCatalogMetadata(
        this StripePaymentEventRequest request,
        IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null || metadata.Count == 0) return request;

        return request with
        {
            PackageId = metadata.GetValueOrDefault(PackageCatalogConstants.StripeMetadata.PackageId, request.PackageId),
            CouponId = metadata.GetValueOrDefault(PackageCatalogConstants.StripeMetadata.CouponId, request.CouponId),
            Quantity = request.Quantity > 0
                ? request.Quantity
                : int.TryParse(metadata.GetValueOrDefault(PackageCatalogConstants.StripeMetadata.Quantity), NumberStyles.Integer, CultureInfo.InvariantCulture, out var quantity)
                    ? quantity
                    : 0,
            ListPrice = decimal.TryParse(metadata.GetValueOrDefault(PackageCatalogConstants.StripeMetadata.ListPrice), NumberStyles.Number, CultureInfo.InvariantCulture, out var listPrice)
                ? listPrice
                : request.ListPrice,
        };
    }

    /// <summary>
    /// Maps a later event on a Stripe subscription onto the add-on lifecycle when the subscription
    /// is an add-on's (its metadata says PaymentType = AddOn). Anything else is returned unchanged.
    /// </summary>
    public static bool IsAddOnSubscription(IReadOnlyDictionary<string, string>? metadata) =>
        metadata is not null
        && metadata.TryGetValue(PaymentConstants.StripeMetadata.PaymentType, out var type)
        && string.Equals(type, PaymentConstants.PaymentTypes.AddOn, StringComparison.OrdinalIgnoreCase);
}
