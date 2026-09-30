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
/// Function 53 – Create Subscription Plan: <see cref="PlanService.CreatePlanAsync"/>.
/// </summary>
public class PlanServiceCreatePlanTests
{
    private const decimal ConfiguredMinimumUsdPrice = 5m;

    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IPlanRepository> _planRepository = new();
    private readonly Mock<ISubscriptionRepository> _subscriptionRepository = new();
    private readonly Mock<IBillingMessagePublisher> _messagePublisher = new();
    private readonly Mock<ILogger<PlanService>> _logger = new();
    private readonly PlanService _service;

    public PlanServiceCreatePlanTests()
    {
        _unitOfWork.Setup(u => u.Plans).Returns(_planRepository.Object);
        _unitOfWork.Setup(u => u.SubscriptionRepository).Returns(_subscriptionRepository.Object);
        _unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        _planRepository
            .Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Plan, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Plan?)null);
        _planRepository
            .Setup(r => r.AddAsync(It.IsAny<Plan>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var pricingConfigService = new Mock<IUsageRateCardAdminService>();
        pricingConfigService
            .Setup(s => s.GetPricingConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(CreatePricingConfig()));

        _service = new PlanService(
            _unitOfWork.Object,
            _logger.Object,
            _messagePublisher.Object,
            pricingConfigService.Object);
    }

    [Fact]
    public async Task CreatePlanAsync_UTCID01_ValidRequest_AddsSavesAndPublishes_EvenWhenPublishFails()
    {
        Plan? added = null;
        _planRepository
            .Setup(r => r.AddAsync(It.IsAny<Plan>(), It.IsAny<CancellationToken>()))
            .Callback<Plan, CancellationToken>((plan, _) => added = plan)
            .Returns(Task.CompletedTask);
        _messagePublisher
            .Setup(p => p.PublishAsync(It.IsAny<string>(), It.IsAny<It.IsAnyType>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("redis down"));

        var result = await _service.CreatePlanAsync(ValidRequest());

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.Slug.Should().Be("pro");
        added.Should().NotBeNull();
        added!.Slug.Should().Be("pro");
        added.Currency.Should().Be("USD");
        _planRepository.Verify(r => r.AddAsync(It.IsAny<Plan>(), It.IsAny<CancellationToken>()), Times.Once);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        _messagePublisher.Verify(
            p => p.PublishAsync(BillingMessageConstants.Notifications.Channel, It.IsAny<It.IsAnyType>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CreatePlanAsync_UTCID02_EmptyName_ReturnsValidationError()
    {
        var result = await _service.CreatePlanAsync(ValidRequest() with { Name = "" });

        AssertValidationFailure(result, ApiMessageConstants.ValidationMessages.PlanNameRequired);
    }

    [Fact]
    public async Task CreatePlanAsync_UTCID03_SlugWithInvalidCharacters_ReturnsValidationError()
    {
        var result = await _service.CreatePlanAsync(ValidRequest() with { Slug = "Pro_Plan!" });

        AssertValidationFailure(result, ApiMessageConstants.ValidationMessages.PlanSlugInvalid);
    }

    [Fact]
    public async Task CreatePlanAsync_UTCID04_UnsupportedCurrency_ReturnsValidationError()
    {
        var result = await _service.CreatePlanAsync(ValidRequest() with { Currency = "EUR" });

        AssertValidationFailure(result, ApiMessageConstants.ValidationMessages.PlanCurrencyInvalid);
    }

    [Fact]
    public async Task CreatePlanAsync_UTCID05_PriceBelowConfiguredMinimum_ReturnsValidationError()
    {
        // Above the hardcoded PlanDefaults minimum (0.50) but below the configured one (5.00):
        // proves the pricing config, not the default, is what is enforced.
        var result = await _service.CreatePlanAsync(ValidRequest() with { Price = 4.99m });

        AssertValidationFailure(
            result,
            string.Format(ApiMessageConstants.ValidationMessages.PlanMinPrice, "USD", ConfiguredMinimumUsdPrice));
    }

    [Fact]
    public async Task CreatePlanAsync_UTCID06_FeaturesNotJsonObjectOrArray_ReturnsValidationError()
    {
        var result = await _service.CreatePlanAsync(ValidRequest() with { Features = "voice_clone: true" });

        AssertValidationFailure(result, ApiMessageConstants.ValidationMessages.PlanFeaturesInvalid);
    }

    [Fact]
    public async Task CreatePlanAsync_UTCID07_DuplicateActiveSlug_ReturnsDuplicatePlanSlug()
    {
        Expression<Func<Plan, bool>>? predicate = null;
        _planRepository
            .Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Plan, bool>>>(), It.IsAny<CancellationToken>()))
            .Callback<Expression<Func<Plan, bool>>, CancellationToken>((p, _) => predicate = p)
            .ReturnsAsync(new Plan { Id = Guid.NewGuid(), Slug = "pro", Name = "Existing" });

        var result = await _service.CreatePlanAsync(ValidRequest());

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.BillingDuplicatePlanSlug);
        result.Error.Should().Be(ApiMessageConstants.ErrorMessages.BillingDuplicatePlanSlug);

        // The lookup is on the normalized slug and ignores soft-deleted plans.
        predicate.Should().NotBeNull();
        var compiled = predicate!.Compile();
        compiled(new Plan { Slug = "pro" }).Should().BeTrue();
        compiled(new Plan { Slug = "pro", DeletedAt = DateTime.UtcNow }).Should().BeFalse();

        _planRepository.Verify(r => r.AddAsync(It.IsAny<Plan>(), It.IsAny<CancellationToken>()), Times.Never);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("add")]
    [InlineData("save")]
    public async Task CreatePlanAsync_UTCID08_PersistenceThrows_ReturnsInternalErrorAndLogs(string failingStep)
    {
        if (failingStep == "add")
        {
            _planRepository
                .Setup(r => r.AddAsync(It.IsAny<Plan>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("db down"));
        }
        else
        {
            _unitOfWork
                .Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("db down"));
        }

        var result = await _service.CreatePlanAsync(ValidRequest());

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.InternalServerError);
        result.Error.Should().Be(ApiMessageConstants.ErrorMessages.BillingInternalError);
        _logger.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<InvalidOperationException>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
        _messagePublisher.Verify(
            p => p.PublishAsync(It.IsAny<string>(), It.IsAny<It.IsAnyType>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private void AssertValidationFailure(Result<PlanDto> result, string expectedMessage)
    {
        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
        result.Error.Should().Be(expectedMessage);
        _planRepository.Verify(r => r.AddAsync(It.IsAny<Plan>(), It.IsAny<CancellationToken>()), Times.Never);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    private static PlanRequest ValidRequest() => new(
        Name: "Pro",
        Slug: "pro",
        Tier: "pro",
        Price: 10m,
        Currency: "USD",
        BillingCycle: SubscriptionConstants.BillingCycles.Monthly,
        CreditsPerCycle: 1000,
        MaxParticipants: 10,
        Features: "{\"voice_clone\":true}",
        SortOrder: 1);

    private static PricingConfigDto CreatePricingConfig() => new(
        FxRateUsdVnd: 26300m,
        CreditValueVnd: 4m,
        MinimumPricePerCreditVnd: 2.60m,
        MinimumContractPriceVnd: 15000m,
        MinimumContractPriceUsd: ConfiguredMinimumUsdPrice,
        SalesUsageWeight: 0.45m,
        SalesMembersWeight: 0.15m,
        SalesLanguagesWeight: 0.15m,
        SalesAiServicesWeight: 0.25m,
        DefaultOverageCapRatio: 0.15m,
        DefaultInvoiceTermsDays: 15m,
        DefaultInvoiceGraceHours: 360m,
        Formula: "",
        ResolverKey: "");
}
