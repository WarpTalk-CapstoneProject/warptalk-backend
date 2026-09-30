using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Mappers;
using WarpTalk.BillingService.Application.Services.PaymentEventHandlers;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.BillingService.Tests.Application.PackageCatalog;

/// <summary>
/// G11: PaymentAppService takes the FIRST handler that claims an event, and
/// CancellationPaymentEventHandler claims every Cancelled/Refunded one — ending the workspace's
/// plan. An add-on ending or a pack refunded must be claimed before it can get there.
/// </summary>
public class CatalogPaymentRoutingTests
{
    private static IReadOnlyList<IPaymentEventHandler> HandlersInRegistrationOrder()
    {
        var uow = Mock.Of<IUnitOfWork>();
        return
        [
            new CreditPackPaymentEventHandler(uow, NullLogger<CreditPackPaymentEventHandler>.Instance),
            new AddOnPaymentEventHandler(uow, NullLogger<AddOnPaymentEventHandler>.Instance),
            new SubscriptionPaymentEventHandler(uow, NullLogger<SubscriptionPaymentEventHandler>.Instance),
            new CancellationPaymentEventHandler(),
            new CreditTopUpPaymentEventHandler(uow, NullLogger<CreditTopUpPaymentEventHandler>.Instance),
        ];
    }

    private static PaymentEventContext Context(string paymentType, string status) => new(
        new StripePaymentEventRequest("", "sub_1", 0, "vnd", Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), paymentType, status),
        Guid.NewGuid(), Guid.NewGuid(), "sub_1", status, Guid.NewGuid(), null, null);

    [Theory]
    [InlineData(PaymentConstants.PaymentTypes.AddOnCancellation, PaymentConstants.PaymentStatuses.Cancelled, typeof(AddOnPaymentEventHandler))]
    [InlineData(PaymentConstants.PaymentTypes.AddOnUpdate, PaymentConstants.PaymentStatuses.SubscriptionUpdated, typeof(AddOnPaymentEventHandler))]
    [InlineData(PaymentConstants.PaymentTypes.AddOnRenewal, PaymentConstants.PaymentStatuses.Paid, typeof(AddOnPaymentEventHandler))]
    [InlineData(PaymentConstants.PaymentTypes.CreditPack, PaymentConstants.PaymentStatuses.Refunded, typeof(CreditPackPaymentEventHandler))]
    [InlineData(PaymentConstants.PaymentTypes.Subscription, PaymentConstants.PaymentStatuses.Cancelled, typeof(SubscriptionPaymentEventHandler))]
    public void Each_event_reaches_its_own_handler_first(string paymentType, string status, Type expected)
    {
        var first = HandlersInRegistrationOrder().First(handler => handler.CanHandle(Context(paymentType, status)));
        first.Should().BeOfType(expected);
    }

    [Fact]
    public void Catalog_metadata_is_read_back_off_the_session()
    {
        var packageId = Guid.NewGuid().ToString();
        var request = new StripePaymentEventRequest("cs_1", "", 90_000, "vnd", "", "", PaymentConstants.PaymentTypes.CreditPack, "paid")
            .WithCatalogMetadata(new Dictionary<string, string>
            {
                [PackageCatalogConstants.StripeMetadata.PackageId] = packageId,
                [PackageCatalogConstants.StripeMetadata.CouponId] = "c",
                [PackageCatalogConstants.StripeMetadata.Quantity] = "3",
                [PackageCatalogConstants.StripeMetadata.ListPrice] = "100000",
            });

        request.PackageId.Should().Be(packageId);
        request.Quantity.Should().Be(3);
        request.ListPrice.Should().Be(100_000m);
        CatalogMetadataMapper.IsAddOnSubscription(new Dictionary<string, string>
        {
            [PaymentConstants.StripeMetadata.PaymentType] = PaymentConstants.PaymentTypes.AddOn,
        }).Should().BeTrue();
        CatalogMetadataMapper.IsAddOnSubscription(new Dictionary<string, string>
        {
            [PaymentConstants.StripeMetadata.PaymentType] = PaymentConstants.PaymentTypes.Subscription,
        }).Should().BeFalse();
    }
}
