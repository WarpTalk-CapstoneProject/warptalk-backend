using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;
using Xunit;

namespace WarpTalk.BillingService.Tests.Application.Services;

/// <summary>
/// WT-878 — <c>POST /payments/checkout</c> sells only the customer-facing payment types that have a
/// server-priced path. Any other type used to fall through to the generic Stripe session, which
/// charged the client's own Amount and wrote the client's PlanSlug on the metadata: an Owner could
/// post "SubscriptionUpdate" + "enterprise" + 15,000 VND and receive a year of Enterprise.
/// </summary>
public class PaymentAppServiceCheckoutAllowlistTests
{
    private readonly Mock<IStripePaymentService> _stripe = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IUsageRateCardRepository> _rateCards = new();
    private readonly List<Plan> _plans = new();
    private readonly List<(CreateCheckoutSessionRequest Request, CheckoutExtras Extras)> _catalogSessions = new();
    private CreateCheckoutSessionRequest? _genericSession;

    public PaymentAppServiceCheckoutAllowlistTests()
    {
        var plans = new Mock<IPlanRepository>();
        plans
            .Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Plan, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Expression<Func<Plan, bool>> predicate, CancellationToken _) => _plans.FirstOrDefault(predicate.Compile()));
        plans
            .Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Plan, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Expression<Func<Plan, bool>> predicate, string _, CancellationToken _) => _plans.FirstOrDefault(predicate.Compile()));
        _unitOfWork.Setup(u => u.Plans).Returns(plans.Object);

        var subscriptions = new Mock<ISubscriptionRepository>();
        subscriptions
            .Setup(r => r.AnyAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _unitOfWork.Setup(u => u.SubscriptionRepository).Returns(subscriptions.Object);

        _stripe
            .Setup(s => s.CreateCatalogCheckoutSessionAsync(It.IsAny<CreateCheckoutSessionRequest>(), It.IsAny<CheckoutExtras>(), It.IsAny<CancellationToken>()))
            .Callback<CreateCheckoutSessionRequest, CheckoutExtras, CancellationToken>((r, e, _) => _catalogSessions.Add((r, e)))
            .ReturnsAsync(Result.Success("https://checkout.stripe.test/c/pay/cs_test_plan"));
        _stripe
            .Setup(s => s.CreateCheckoutSessionAsync(It.IsAny<CreateCheckoutSessionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<CreateCheckoutSessionRequest, CancellationToken>((r, _) => _genericSession = r)
            .ReturnsAsync(Result.Success("https://checkout.stripe.test/c/pay/cs_test_generic"));

        _rateCards
            .Setup(r => r.ReadPricingConfigValueAsync(It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(4m);
    }

    private PaymentAppService CreateService() => new(
        _stripe.Object,
        _unitOfWork.Object,
        Mock.Of<ILogger<PaymentAppService>>(),
        Mock.Of<IBillingMessagePublisher>(),
        Array.Empty<IPaymentEventHandler>(),
        Mock.Of<IWorkspaceClient>(),
        _rateCards.Object);

    private Plan AddPlan(string slug, decimal price, bool isActive = true)
    {
        var plan = new Plan
        {
            Id = Guid.NewGuid(),
            Slug = slug,
            Name = slug,
            Price = price,
            Currency = PaymentConstants.Currencies.VndAccounting,
            BillingCycle = SubscriptionConstants.BillingCycles.Monthly,
            CreditsPerCycle = 700_000,
            IsActive = isActive,
        };
        _plans.Add(plan);
        return plan;
    }

    /// <summary>The request from the report, verbatim apart from the type.</summary>
    private static CreateCheckoutSessionRequest Exploit(string paymentType, decimal amount = 15_000m) => new(
        UserId: Guid.NewGuid(),
        WorkspaceId: Guid.NewGuid(),
        Amount: amount,
        Currency: PaymentConstants.Currencies.Vnd,
        PaymentType: paymentType,
        PlanSlug: "enterprise",
        BillingCycle: PaymentConstants.BillingCycles.Yearly);

    private void VerifyNoStripeSession()
    {
        _stripe.Verify(s => s.CreateCheckoutSessionAsync(It.IsAny<CreateCheckoutSessionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _stripe.Verify(s => s.CreateCatalogCheckoutSessionAsync(It.IsAny<CreateCheckoutSessionRequest>(), It.IsAny<CheckoutExtras>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(PaymentConstants.PaymentTypes.SubscriptionUpdate)]
    [InlineData(PaymentConstants.PaymentTypes.SubscriptionRenewal)]
    [InlineData(PaymentConstants.PaymentTypes.InvoicePayment)]
    [InlineData(PaymentConstants.PaymentTypes.AddOnRenewal)]
    [InlineData(PaymentConstants.PaymentTypes.AddOnUpdate)]
    [InlineData("Renewal")]
    [InlineData("enterprise")]
    [InlineData("")]
    public async Task A_type_outside_the_allowlist_is_refused_before_any_Stripe_session(string paymentType)
    {
        AddPlan("enterprise", 5_000_000m);

        var result = await CreateService().CreateCheckoutSessionAsync(Exploit(paymentType));

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
        result.Error.Should().Be(PaymentConstants.PaymentTypes.CheckoutTypeNotAllowedMessage);
        VerifyNoStripeSession();
    }

    [Fact]
    public async Task A_null_type_is_refused()
    {
        var result = await CreateService().CreateCheckoutSessionAsync(Exploit(null!));

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
        VerifyNoStripeSession();
    }

    [Theory]
    [InlineData(15_000)]
    [InlineData(1)]
    [InlineData(999_999_999)]
    public async Task A_plan_checkout_is_priced_from_the_plan_whatever_Amount_the_client_sent(int clientAmount)
    {
        AddPlan("enterprise", 5_000_000m);

        var result = await CreateService().CreateCheckoutSessionAsync(
            Exploit(PaymentConstants.PaymentTypes.Subscription, clientAmount));

        result.IsSuccess.Should().BeTrue(result.Error);
        var (request, extras) = _catalogSessions.Single();
        var yearly = 5_000_000m * 12m * 0.79m;
        request.Amount.Should().Be(yearly);
        request.Currency.Should().Be(PaymentConstants.Currencies.Vnd);
        extras.Line!.UnitAmount.Should().Be(yearly, "the amount Stripe charges is the line's, and the line is the server's");
        _genericSession.Should().BeNull("a plan never reaches the generic session that charges request.Amount");
    }

    [Fact]
    public async Task The_type_is_matched_case_insensitively_and_forwarded_in_its_canonical_spelling()
    {
        AddPlan("enterprise", 5_000_000m);

        var result = await CreateService().CreateCheckoutSessionAsync(Exploit("subscription"));

        result.IsSuccess.Should().BeTrue(result.Error);
        _catalogSessions.Single().Request.PaymentType.Should().Be(PaymentConstants.PaymentTypes.Subscription);
    }

    [Fact]
    public async Task An_inactive_plan_cannot_be_bought()
    {
        AddPlan("enterprise", 5_000_000m, isActive: false);

        var result = await CreateService().CreateCheckoutSessionAsync(Exploit(PaymentConstants.PaymentTypes.Subscription));

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.BillingPlanNotFound);
        VerifyNoStripeSession();
    }

    [Fact]
    public async Task A_top_up_is_still_priced_from_its_credits_and_carries_no_plan()
    {
        var request = Exploit(PaymentConstants.PaymentTypes.CreditTopUp, amount: 1m) with { Credits = 10_000 };

        var result = await CreateService().CreateCheckoutSessionAsync(request);

        result.IsSuccess.Should().BeTrue(result.Error);
        _genericSession.Should().NotBeNull();
        _genericSession!.Amount.Should().Be(40_000m);
        _genericSession.PaymentType.Should().Be(PaymentConstants.PaymentTypes.CreditTopUp);
        _genericSession.PlanSlug.Should().BeEmpty("a top-up must not stamp a plan slug on the Stripe metadata");
    }

    [Theory]
    [InlineData(PaymentConstants.PaymentTypes.CreditPack)]
    [InlineData(PaymentConstants.PaymentTypes.AddOn)]
    public async Task A_catalog_type_without_the_catalog_is_refused_rather_than_sold_at_the_clients_price(string paymentType)
    {
        var result = await CreateService().CreateCheckoutSessionAsync(Exploit(paymentType) with { PackageId = Guid.NewGuid() });

        result.IsSuccess.Should().BeFalse();
        VerifyNoStripeSession();
    }
}
