using FluentAssertions;
using Moq;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Tests.Application.Recurring;

/// <summary>
/// #466 — a plan bought with auto-renew on is a real Stripe Subscription on the plan's recurring
/// Price; bought with it off, it is one paid period. The checkout that completes records which.
/// </summary>
public class StripeRecurringCheckoutTests
{
    private static CreateCheckoutSessionRequest PlanCheckout(Guid workspaceId, bool? autoRenew, string cycle = "monthly", decimal clientAmount = 499_000m) => new(
        UserId: Guid.NewGuid(),
        WorkspaceId: workspaceId,
        Amount: clientAmount,
        Currency: "vnd",
        PaymentType: PaymentConstants.PaymentTypes.Subscription,
        PlanSlug: "startup",
        BillingCycle: cycle,
        AutoRenew: autoRenew);

    private static (RecurringBillingWorld World, Mock<IStripePaymentService> Stripe, List<(CreateCheckoutSessionRequest Request, CheckoutExtras Extras)> Sessions) Arrange()
    {
        var world = new RecurringBillingWorld();
        world.AddPlan(price: 499_000m);
        world.Stripe
            .Setup(s => s.EnsurePlanPriceAsync(It.IsAny<Plan>(), It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Plan plan, string cycle, decimal _, string _, CancellationToken _) =>
            {
                plan.StripeProductId = "prod_test_startup";
                return Result.Success($"price_test_{cycle}");
            });

        var sessions = new List<(CreateCheckoutSessionRequest, CheckoutExtras)>();
        var stripe = new Mock<IStripePaymentService>();
        stripe
            .Setup(s => s.CreateCatalogCheckoutSessionAsync(It.IsAny<CreateCheckoutSessionRequest>(), It.IsAny<CheckoutExtras>(), It.IsAny<CancellationToken>()))
            .Callback<CreateCheckoutSessionRequest, CheckoutExtras, CancellationToken>((r, e, _) => sessions.Add((r, e)))
            .ReturnsAsync(Result.Success("https://checkout.stripe.test/c/pay/cs_test_1"));
        return (world, stripe, sessions);
    }

    [Fact]
    public async Task Auto_renew_on_sells_a_Stripe_subscription_on_the_plans_recurring_price()
    {
        var (world, stripe, sessions) = Arrange();

        var result = await world.PaymentApp(stripe).CreateCheckoutSessionAsync(PlanCheckout(Guid.NewGuid(), autoRenew: null));

        result.IsSuccess.Should().BeTrue();
        var (request, extras) = sessions.Single();
        extras.Line!.StripePriceId.Should().Be("price_test_monthly");
        extras.Line.RecurringInterval.Should().Be(PaymentConstants.PriceIntervals.Month, "a recurring line makes the session mode=subscription");
        extras.Metadata[PaymentConstants.StripeMetadata.AutoRenew].Should().Be("true");
        request.Amount.Should().Be(499_000m);
        world.Saves.Should().BeGreaterThan(0, "the plan's new Stripe ids are persisted");
    }

    [Fact]
    public async Task Auto_renew_off_sells_one_period_as_a_one_off_payment()
    {
        var (world, stripe, sessions) = Arrange();

        var result = await world.PaymentApp(stripe).CreateCheckoutSessionAsync(PlanCheckout(Guid.NewGuid(), autoRenew: false));

        result.IsSuccess.Should().BeTrue();
        var (_, extras) = sessions.Single();
        extras.Line!.RecurringInterval.Should().BeNull("no recurring line: mode=payment, nothing saved to charge again");
        extras.Line.StripePriceId.Should().BeNull();
        extras.Metadata[PaymentConstants.StripeMetadata.AutoRenew].Should().Be("false");
        world.Stripe.Verify(s => s.EnsurePlanPriceAsync(It.IsAny<Plan>(), It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task The_price_is_the_servers_not_the_clients()
    {
        var (world, stripe, sessions) = Arrange();

        await world.PaymentApp(stripe).CreateCheckoutSessionAsync(PlanCheckout(Guid.NewGuid(), autoRenew: true, cycle: "yearly", clientAmount: 1m));

        var (request, extras) = sessions.Single();
        request.Amount.Should().Be(499_000m * 12m * 0.79m, "yearly is twelve months at the plans page's 79%");
        extras.Line!.UnitAmount.Should().Be(request.Amount);
        extras.Line.RecurringInterval.Should().Be(PaymentConstants.PriceIntervals.Year);
        world.Stripe.Verify(s => s.EnsurePlanPriceAsync(It.IsAny<Plan>(), "yearly", 499_000m * 12m * 0.79m, "vnd", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_subscription_mode_checkout_links_the_row_to_Stripe_and_stops_the_plan_it_replaced()
    {
        var world = new RecurringBillingWorld();
        var plan = world.AddPlan(creditsPerCycle: 100_000);
        var existing = world.AddSubscription(plan, SubscriptionConstants.RenewalModes.Stripe, DateTime.UtcNow.AddDays(3), stripeSubscriptionId: "sub_test_old");

        var result = await world.PaymentApp().ProcessPaymentEventAsync(Completed(existing.WorkspaceId, "cs_test_upgrade", "sub_test_new"));

        result.IsSuccess.Should().BeTrue();
        existing.RenewalMode.Should().Be(SubscriptionConstants.RenewalModes.Stripe);
        existing.StripeSubscriptionId.Should().Be("sub_test_new");
        existing.StripeCustomerId.Should().Be("cus_test_new");
        existing.AutoRenew.Should().BeTrue();
        world.Stripe.Verify(s => s.CancelNowAsync("sub_test_old", It.IsAny<CancellationToken>()), Times.Once,
            "the replaced plan's Stripe subscription would otherwise keep charging the card");
        world.Stripe.Verify(s => s.CancelNowAsync("sub_test_new", It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_one_off_checkout_activates_a_plan_nothing_renews()
    {
        var world = new RecurringBillingWorld();
        world.AddPlan(creditsPerCycle: 100_000);
        var workspaceId = Guid.NewGuid();

        var result = await world.PaymentApp().ProcessPaymentEventAsync(Completed(workspaceId, "cs_test_once", stripeSubscriptionId: string.Empty));

        result.IsSuccess.Should().BeTrue();
        var row = world.Subscriptions.Single(s => s.WorkspaceId == workspaceId);
        row.RenewalMode.Should().Be(SubscriptionConstants.RenewalModes.None);
        row.AutoRenew.Should().BeFalse();
        row.StripeSubscriptionId.Should().BeNull();
        world.GrantedCredits(row).Should().Be(100_000);
    }

    private static StripePaymentEventRequest Completed(Guid workspaceId, string sessionId, string stripeSubscriptionId) => new(
        StripeSessionId: sessionId,
        PaymentIntentId: string.Empty,
        Amount: 499_000m,
        Currency: "vnd",
        UserIdStr: Guid.NewGuid().ToString(),
        WorkspaceIdStr: workspaceId.ToString(),
        PaymentType: PaymentConstants.PaymentTypes.Subscription,
        Status: PaymentConstants.PaymentStatuses.Paid,
        PlanSlug: "startup",
        BillingCycle: "monthly",
        StripeSubscriptionId: stripeSubscriptionId,
        StripeCustomerId: string.IsNullOrEmpty(stripeSubscriptionId) ? string.Empty : "cus_test_new");
}
