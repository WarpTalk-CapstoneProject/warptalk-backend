using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;
using Xunit;

namespace WarpTalk.BillingService.Tests.Application.Services;

/// <summary>
/// Function 54 – Create Checkout Session, credit top-up branch of
/// <see cref="PaymentAppService.CreateCheckoutSessionAsync"/>.
/// </summary>
public class PaymentAppServiceCreditTopUpCheckoutTests
{
    private const string CreditValueConfigKey = "credit_value_vnd";

    private readonly Mock<IStripePaymentService> _stripePaymentService = new();
    private readonly Mock<IUsageRateCardRepository> _rateCards = new();
    private readonly Mock<ILogger<PaymentAppService>> _logger = new();
    private readonly PaymentAppService _service;
    private CreateCheckoutSessionRequest? _sentToStripe;

    public PaymentAppServiceCreditTopUpCheckoutTests()
    {
        _stripePaymentService
            .Setup(s => s.CreateCheckoutSessionAsync(It.IsAny<CreateCheckoutSessionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<CreateCheckoutSessionRequest, CancellationToken>((r, _) => _sentToStripe = r)
            .ReturnsAsync(Result.Success("https://checkout.stripe.test/session"));

        // backend#467: a top-up is sold only on top of a live plan; these tests buy for a workspace
        // that has one (the refusal is pinned in PaymentAppServiceTests).
        var subscriptions = new Mock<ISubscriptionRepository>();
        subscriptions
            .Setup(r => r.AnyAsync(It.IsAny<System.Linq.Expressions.Expression<Func<WarpTalk.BillingService.Domain.Entities.Subscription, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(u => u.SubscriptionRepository).Returns(subscriptions.Object);

        _service = new PaymentAppService(
            _stripePaymentService.Object,
            unitOfWork.Object,
            _logger.Object,
            Mock.Of<IBillingMessagePublisher>(),
            Array.Empty<IPaymentEventHandler>(),
            Mock.Of<IWorkspaceClient>(),
            _rateCards.Object);
    }

    [Fact]
    public async Task CreateCheckoutSessionAsync_UTCID03_CreditTopUp_OverwritesAmountFromCreditsAndCallsStripe()
    {
        SetupCreditValue(4m);
        var request = TopUpRequest(credits: 10000, clientAmount: 1m);

        var result = await _service.CreateCheckoutSessionAsync(request);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("https://checkout.stripe.test/session");
        _sentToStripe.Should().NotBeNull();
        _sentToStripe!.Amount.Should().Be(40000m, "client Amount is discarded and recomputed as Credits * credit_value_vnd");
        _sentToStripe.Currency.Should().Be(PaymentConstants.Currencies.Vnd);
        // Fields StripePaymentService turns into session metadata.
        _sentToStripe.Credits.Should().Be(10000);
        _sentToStripe.PaymentType.Should().Be(PaymentConstants.PaymentTypes.CreditTopUp);
        _sentToStripe.WorkspaceId.Should().Be(request.WorkspaceId);
        _sentToStripe.UserId.Should().Be(request.UserId);
        _rateCards.Verify(
            r => r.ReadPricingConfigValueAsync(CreditValueConfigKey, SubscriptionConstants.RateCardDefaults.CreditValueVnd, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CreateCheckoutSessionAsync_UTCID04_CreditsBelowMinimum_ReturnsValidationError()
    {
        SetupCreditValue(4m);

        var result = await _service.CreateCheckoutSessionAsync(TopUpRequest(credits: 1499));

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
        result.Error.Should().Be(string.Format(BillingMessageConstants.ErrorMessages.CreditTopUpBelowMinimum, 1500));
        _rateCards.Verify(
            r => r.ReadPricingConfigValueAsync(It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()),
            Times.Never);
        VerifyStripeNeverCalled();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task CreateCheckoutSessionAsync_UTCID06_CreditValueNotPositive_ReturnsInternalErrorAndLogs(int creditValue)
    {
        SetupCreditValue(creditValue);

        var result = await _service.CreateCheckoutSessionAsync(TopUpRequest(credits: 10000));

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.InternalServerError);
        result.Error.Should().Be(BillingMessageConstants.ErrorMessages.CreditTopUpRateUnavailable);
        _logger.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains("credit_topup_rate_unavailable")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
        VerifyStripeNeverCalled();
    }

    [Fact]
    public async Task CreateCheckoutSessionAsync_UTCID07_CreditsExactlyMinimum_Succeeds()
    {
        SetupCreditValue(4m);

        var result = await _service.CreateCheckoutSessionAsync(TopUpRequest(credits: 1500));

        result.IsSuccess.Should().BeTrue();
        _sentToStripe.Should().NotBeNull();
        _sentToStripe!.Amount.Should().Be(6000m);
        _sentToStripe.Currency.Should().Be(PaymentConstants.Currencies.Vnd);
        _sentToStripe.Credits.Should().Be(1500);
    }

    private void SetupCreditValue(decimal value) =>
        _rateCards
            .Setup(r => r.ReadPricingConfigValueAsync(CreditValueConfigKey, It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(value);

    private void VerifyStripeNeverCalled() =>
        _stripePaymentService.Verify(
            s => s.CreateCheckoutSessionAsync(It.IsAny<CreateCheckoutSessionRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);

    private static CreateCheckoutSessionRequest TopUpRequest(int credits, decimal clientAmount = 0m) => new(
        UserId: Guid.NewGuid(),
        WorkspaceId: Guid.NewGuid(),
        Amount: clientAmount,
        Currency: PaymentConstants.Currencies.Usd,
        PaymentType: PaymentConstants.PaymentTypes.CreditTopUp,
        Credits: credits);
}
