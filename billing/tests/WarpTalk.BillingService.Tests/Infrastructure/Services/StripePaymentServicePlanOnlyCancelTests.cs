using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using Stripe;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Infrastructure.Services;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Tests.Infrastructure.Services;

/// <summary>
/// WT-878 — cancelling the plan in Stripe must never touch an add-on's subscription. The legacy
/// search matched every active subscription carrying the workspace id, add-ons included.
/// </summary>
public class StripePaymentServicePlanOnlyCancelTests
{
    private static IConfiguration ConfiguredStripe() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            [PaymentConstants.StripeConfigKeys.SecretKey] = "sk_test_wt878",
        })
        .Build();

    private static Stripe.Subscription StripeSub(string id, Dictionary<string, string>? metadata) =>
        new() { Id = id, Status = "active", Metadata = metadata ?? new Dictionary<string, string>() };

    [Fact]
    public async Task LegacyCancel_TouchesOnlyThePlanSubscription_NeverAnAddOn()
    {
        var workspaceId = Guid.NewGuid();
        var sdk = new Mock<IStripeSdkClient>();
        sdk.Setup(s => s.SearchSubscriptionsAsync(It.IsAny<SubscriptionSearchOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StripeSearchResult<Stripe.Subscription>
            {
                Data = new List<Stripe.Subscription>
                {
                    StripeSub("sub_plan", new()
                    {
                        [PaymentConstants.StripeMetadata.WorkspaceId] = workspaceId.ToString(),
                        [PaymentConstants.StripeMetadata.PaymentType] = PaymentConstants.PaymentTypes.Subscription,
                    }),
                    StripeSub("sub_addon", new()
                    {
                        [PaymentConstants.StripeMetadata.WorkspaceId] = workspaceId.ToString(),
                        [PaymentConstants.StripeMetadata.PaymentType] = PaymentConstants.PaymentTypes.AddOn,
                        [PackageCatalogConstants.StripeMetadata.PackageId] = Guid.NewGuid().ToString(),
                    }),
                    StripeSub("sub_addon_untyped", new()
                    {
                        [PaymentConstants.StripeMetadata.WorkspaceId] = workspaceId.ToString(),
                        [PackageCatalogConstants.StripeMetadata.PackageId] = Guid.NewGuid().ToString(),
                    }),
                },
            });
        sdk.Setup(s => s.UpdateSubscriptionAsync(It.IsAny<string>(), It.IsAny<SubscriptionUpdateOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Stripe.Subscription { Id = "sub_plan", Status = "active" });

        var result = await new StripePaymentService(ConfiguredStripe(), sdk.Object).CancelSubscriptionAsync(workspaceId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeTrue();
        sdk.Verify(s => s.UpdateSubscriptionAsync("sub_plan", It.Is<SubscriptionUpdateOptions>(o => o.CancelAtPeriodEnd == true), It.IsAny<CancellationToken>()), Times.Once);
        sdk.Verify(s => s.UpdateSubscriptionAsync("sub_addon", It.IsAny<SubscriptionUpdateOptions>(), It.IsAny<CancellationToken>()), Times.Never);
        sdk.Verify(s => s.UpdateSubscriptionAsync("sub_addon_untyped", It.IsAny<SubscriptionUpdateOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task LegacyCancel_WhenStripeThrows_IsAFailure()
    {
        var sdk = new Mock<IStripeSdkClient>();
        sdk.Setup(s => s.SearchSubscriptionsAsync(It.IsAny<SubscriptionSearchOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new StripeException("down"));

        var result = await new StripePaymentService(ConfiguredStripe(), sdk.Object).CancelSubscriptionAsync(Guid.NewGuid());

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.BillingExternalServiceError);
    }

    [Fact]
    public async Task LegacyCancel_WithoutAStripeKey_HasNothingToCancel()
    {
        var sdk = new Mock<IStripeSdkClient>();

        var result = await new StripePaymentService(new ConfigurationBuilder().Build(), sdk.Object).CancelSubscriptionAsync(Guid.NewGuid());

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeFalse();
        sdk.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SetPlanSubscriptionCancelAtPeriodEnd_UpdatesExactlyThatSubscription(bool cancelAtPeriodEnd)
    {
        var sdk = new Mock<IStripeSdkClient>();
        sdk.Setup(s => s.UpdateSubscriptionAsync("sub_plan", It.IsAny<SubscriptionUpdateOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Stripe.Subscription { Id = "sub_plan", Status = "active" });

        var result = await new StripePaymentService(ConfiguredStripe(), sdk.Object)
            .SetPlanSubscriptionCancelAtPeriodEndAsync("sub_plan", cancelAtPeriodEnd);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("active");
        sdk.Verify(s => s.UpdateSubscriptionAsync("sub_plan", It.Is<SubscriptionUpdateOptions>(o => o.CancelAtPeriodEnd == cancelAtPeriodEnd), It.IsAny<CancellationToken>()), Times.Once);
        sdk.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SetPlanSubscriptionCancelAtPeriodEnd_WhenStripeThrows_IsAFailure()
    {
        var sdk = new Mock<IStripeSdkClient>();
        sdk.Setup(s => s.UpdateSubscriptionAsync(It.IsAny<string>(), It.IsAny<SubscriptionUpdateOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new StripeException("card_declined"));

        var result = await new StripePaymentService(ConfiguredStripe(), sdk.Object)
            .SetPlanSubscriptionCancelAtPeriodEndAsync("sub_plan", true);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.BillingExternalServiceError);
    }
}
