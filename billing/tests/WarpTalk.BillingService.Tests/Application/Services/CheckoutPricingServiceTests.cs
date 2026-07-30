using FluentAssertions;
using Moq;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Tests.Application.Services;

public class CheckoutPricingServiceTests
{
    private readonly Mock<IPlanService> _plans = new();
    private readonly CheckoutPricingService _service;

    public CheckoutPricingServiceTests()
    {
        _service = new CheckoutPricingService(_plans.Object);
    }

    [Fact]
    public async Task ResolveAsync_Subscription_UsesServerPlanPriceInsteadOfClientAmount()
    {
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        _plans.Setup(x => x.GetPlanBySlugAsync("pro", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new PlanDto(
                Guid.NewGuid(), "Pro", "pro", "pro", 499_000m, "VND", "monthly",
                50_000, 50, 10, true, true, true, false, "{}", 1, true)));

        var result = await _service.ResolveAsync(
            new CreateCheckoutSessionRequest(
                userId, workspaceId, 1m, "USD", "Subscription", "pro", "yearly"),
            userId);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Amount.Should().Be(499_000m);
        result.Value.Currency.Should().Be("vnd");
        result.Value.BillingCycle.Should().Be("monthly");
    }

    [Fact]
    public async Task ResolveAsync_RejectsUserIdThatDoesNotMatchAuthenticatedUser()
    {
        var result = await _service.ResolveAsync(
            new CreateCheckoutSessionRequest(
                Guid.NewGuid(), Guid.NewGuid(), 100_000m, "VND", "CreditTopUp"),
            Guid.NewGuid());

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.Forbidden);
    }

    [Theory]
    [InlineData(9_999)]
    [InlineData(10_000)]
    [InlineData(10_000_001)]
    public async Task ResolveAsync_RejectsTopUpOutsideAllowedRange(decimal amount)
    {
        var userId = Guid.NewGuid();

        var result = await _service.ResolveAsync(
            new CreateCheckoutSessionRequest(
                userId, Guid.NewGuid(), amount, "VND", "CreditTopUp"),
            userId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
    }

    [Fact]
    public async Task ResolveAsync_AcceptsStripeSafeMinimumVndTopUp()
    {
        var userId = Guid.NewGuid();

        var result = await _service.ResolveAsync(
            new CreateCheckoutSessionRequest(
                userId, Guid.NewGuid(), 15_000m, "VND", "CreditTopUp"),
            userId);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Amount.Should().Be(15_000m);
    }
}
