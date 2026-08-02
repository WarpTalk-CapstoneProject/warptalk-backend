using WarpTalk.BillingService.Domain.Constants;
using System;
using System.Linq.Expressions;
using System.Threading;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;
using Xunit;

namespace WarpTalk.BillingService.Tests.Application.Services;

public class PlanServiceTests
{
    private readonly Mock<IUnitOfWork> _mockUnitOfWork;
    private readonly Mock<IPlanRepository> _mockPlanRepo;
    private readonly Mock<ISubscriptionRepository> _mockSubRepo;
    private readonly PlanService _planService;

    public PlanServiceTests()
    {
        _mockUnitOfWork = new Mock<IUnitOfWork>();
        _mockPlanRepo = new Mock<IPlanRepository>();
        _mockSubRepo = new Mock<ISubscriptionRepository>();

        _mockUnitOfWork.Setup(u => u.PlanRepository).Returns(_mockPlanRepo.Object);
        _mockUnitOfWork.Setup(u => u.SubscriptionRepository).Returns(_mockSubRepo.Object);

        _planService = new PlanService(
            _mockUnitOfWork.Object,
            new Mock<ILogger<PlanService>>().Object,
            new Mock<IBillingMessagePublisher>().Object,
            CreatePricingConfigService());
    }

    private static IUsageRateCardAdminService CreatePricingConfigService()
    {
        var service = new Mock<IUsageRateCardAdminService>();
        service
            .Setup(s => s.GetPricingConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(CreatePricingConfig()));
        return service.Object;
    }

    private static PricingConfigDto CreatePricingConfig() => new(
        FxRateUsdVnd: 26300m,
        CreditValueVnd: 4m,
        MinimumPricePerCreditVnd: 2.60m,
        MinimumContractPriceVnd: 15000m,
        MinimumContractPriceUsd: 0.50m,
        SalesUsageWeight: 0.45m,
        SalesMembersWeight: 0.15m,
        SalesLanguagesWeight: 0.15m,
        SalesAiServicesWeight: 0.25m,
        DefaultOverageCapRatio: 0.15m,
        DefaultInvoiceTermsDays: 15m,
        DefaultInvoiceGraceHours: 360m,
        Formula: "",
        ResolverKey: "");

    // ─────────────────────────────────────────────
    //  Plan CRUD Tests
    //  NOTE: PlanRequest validation requires:
    //    - Currency == "USD" (uppercase, enforced by ValidatePlanRequest)
    //    - Slug matches ^[a-z0-9]+(?:-[a-z0-9]+)*$ (lowercase)
    //    - Price >= 0.50
    // ─────────────────────────────────────────────

    [Fact]
    public async Task CreatePlanAsync_ShouldCreatePlan_WhenValidRequest()
    {
        var request = new PlanRequest("Gold", "gold-tier", "Enterprise", 199.99m, "USD", "monthly", 1000, 10, "{}", 0);
        _mockPlanRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Plan, bool>>>(), default))
            .ReturnsAsync((Plan?)null);

        var result = await _planService.CreatePlanAsync(request);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Name.Should().Be("Gold");
        result.Value.Slug.Should().Be("gold-tier");
        _mockPlanRepo.Verify(r => r.AddAsync(It.Is<Plan>(p => p.Name == "Gold" && p.Slug == "gold-tier"), default), Times.Once);
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(default), Times.Once);
    }

    [Fact]
    public async Task CreatePlanAsync_ShouldReturnFailure_WhenDuplicateSlug()
    {
        var request = new PlanRequest("Gold", "gold-tier", "Enterprise", 199.99m, "USD", "monthly", 1000, 10, "{}", 0);
        var existing = new Plan { Id = Guid.NewGuid(), Name = "Gold Plan", Slug = "gold-tier" };

        _mockPlanRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Plan, bool>>>(), default))
            .ReturnsAsync(existing);

        var result = await _planService.CreatePlanAsync(request);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.BillingDuplicatePlanSlug); // "BILLING_DUPLICATE_PLAN_SLUG"
    }

    [Fact]
    public async Task CreatePlanAsync_ShouldReturnFailure_WhenBillingCycleIsSemiannual()
    {
        var request = new PlanRequest("Gold", "gold-tier", "Enterprise", 199.99m, "USD", "semiannual", 1000, 10, "{}", 0);

        var result = await _planService.CreatePlanAsync(request);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
        result.Error.Should().Be(ApiMessageConstants.ValidationMessages.PlanBillingCycleInvalid);
        _mockPlanRepo.Verify(r => r.AddAsync(It.IsAny<Plan>(), default), Times.Never);
    }

    [Fact]
    public async Task CreatePlanAsync_ShouldMapPhase2PlanDefaults_WhenValidVndRequest()
    {
        var request = new PlanRequest(
            "Enterprise",
            "enterprise",
            "Enterprise",
            1_900_000m,
            "VND",
            "monthly",
            700_000,
            500,
            "{}",
            1,
            OverageCapCredits: 105_000,
            OveragePricePerCredit: 4.0000m,
            LowBalanceThresholdCredits: 140_000,
            RolloverCapCredits: 700_000,
            InvoiceTermsDays: 15,
            InvoiceGraceHours: 360);

        _mockPlanRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Plan, bool>>>(), default))
            .ReturnsAsync((Plan?)null);

        var result = await _planService.CreatePlanAsync(request);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Currency.Should().Be("VND");
        result.Value.OverageCapCredits.Should().Be(105_000);
        result.Value.OveragePricePerCredit.Should().Be(4.0000m);
        result.Value.LowBalanceThresholdCredits.Should().Be(140_000);
        result.Value.RolloverCapCredits.Should().Be(700_000);
        result.Value.InvoiceTermsDays.Should().Be(15);
        result.Value.InvoiceGraceHours.Should().Be(360);
        _mockPlanRepo.Verify(
            r => r.AddAsync(
                It.Is<Plan>(p =>
                    p.Slug == "enterprise" &&
                    p.OverageCapCredits == 105_000 &&
                    p.LowBalanceThresholdCredits == 140_000 &&
                    p.RolloverCapCredits == 700_000),
                default),
            Times.Once);
    }

    [Fact]
    public async Task CreatePlanAsync_ShouldReturnFailure_WhenLowBalanceThresholdDoesNotWarnBeforeOverage()
    {
        var request = new PlanRequest(
            "Enterprise",
            "enterprise",
            "Enterprise",
            1_900_000m,
            "VND",
            "monthly",
            700_000,
            500,
            "{}",
            1,
            OverageCapCredits: 105_000,
            LowBalanceThresholdCredits: 105_000);

        var result = await _planService.CreatePlanAsync(request);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
    }

    [Fact]
    public async Task CreatePlanAsync_ShouldReturnFailure_WhenVndPricePerCreditIsBelowFloor()
    {
        var request = new PlanRequest(
            "Enterprise",
            "enterprise",
            "Enterprise",
            1_700_000m,
            "VND",
            "monthly",
            700_000,
            500,
            "{}",
            1,
            OverageCapCredits: 105_000,
            OveragePricePerCredit: 4.0000m,
            LowBalanceThresholdCredits: 140_000,
            RolloverCapCredits: 700_000,
            InvoiceTermsDays: 15,
            InvoiceGraceHours: 360);

        var result = await _planService.CreatePlanAsync(request);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
        result.Error.Should().Be(ApiMessageConstants.ValidationMessages.PlanEffectivePriceFloorInvalid);
    }

    [Fact]
    public async Task CreatePlanAsync_ShouldReturnFailure_WhenLowBalanceThresholdReachesCommitment()
    {
        var request = new PlanRequest(
            "Enterprise",
            "enterprise",
            "Enterprise",
            1_900_000m,
            "VND",
            "monthly",
            700_000,
            500,
            "{}",
            1,
            OverageCapCredits: 105_000,
            OveragePricePerCredit: 4.0000m,
            LowBalanceThresholdCredits: 700_000,
            RolloverCapCredits: 700_000,
            InvoiceTermsDays: 15,
            InvoiceGraceHours: 360);

        var result = await _planService.CreatePlanAsync(request);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
        result.Error.Should().Be(ApiMessageConstants.ValidationMessages.PlanLowBalanceThresholdTooHigh);
    }

    [Fact]
    public async Task CreatePlanAsync_ShouldReturnFailure_WhenOverageCapExceedsCommitment()
    {
        var request = new PlanRequest(
            "Enterprise",
            "enterprise",
            "Enterprise",
            1_900_000m,
            "VND",
            "monthly",
            700_000,
            500,
            "{}",
            1,
            OverageCapCredits: 700_001,
            OveragePricePerCredit: 4.0000m,
            LowBalanceThresholdCredits: 700_002,
            RolloverCapCredits: 700_000,
            InvoiceTermsDays: 15,
            InvoiceGraceHours: 360);

        var result = await _planService.CreatePlanAsync(request);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
        result.Error.Should().Be(ApiMessageConstants.ValidationMessages.PlanOverageCapTooHigh);
    }

    [Fact]
    public async Task CreatePlanAsync_ShouldReturnFailure_WhenRolloverCapExceedsCommitment()
    {
        var request = new PlanRequest(
            "Enterprise",
            "enterprise",
            "Enterprise",
            1_900_000m,
            "VND",
            "monthly",
            700_000,
            500,
            "{}",
            1,
            OverageCapCredits: 105_000,
            OveragePricePerCredit: 4.0000m,
            LowBalanceThresholdCredits: 140_000,
            RolloverCapCredits: 700_001,
            InvoiceTermsDays: 15,
            InvoiceGraceHours: 360);

        var result = await _planService.CreatePlanAsync(request);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
        result.Error.Should().Be(ApiMessageConstants.ValidationMessages.PlanRolloverCapTooHigh);
    }

    [Fact]
    public async Task CreatePlanAsync_ShouldReturnFailure_WhenMaxLanguagesExceedsConfiguredLimit()
    {
        var request = new PlanRequest(
            "Enterprise",
            "enterprise",
            "Enterprise",
            1_900_000m,
            "VND",
            "monthly",
            700_000,
            500,
            "{}",
            1,
            OverageCapCredits: 105_000,
            OveragePricePerCredit: 4.0000m,
            LowBalanceThresholdCredits: 140_000,
            RolloverCapCredits: 700_000,
            InvoiceTermsDays: 15,
            InvoiceGraceHours: 360,
            MaxLanguages: SubscriptionConstants.FeatureAccess.DefaultMaxLanguages + 1);

        var result = await _planService.CreatePlanAsync(request);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
        result.Error.Should().Be(ApiMessageConstants.ValidationMessages.PlanMaxLanguagesInvalid);
    }

    [Fact]
    public async Task CreatePlanAsync_ShouldReturnFailure_WhenOveragePriceIsZeroForEnabledOverage()
    {
        var request = new PlanRequest(
            "Enterprise",
            "enterprise",
            "Enterprise",
            1_900_000m,
            "VND",
            "monthly",
            700_000,
            500,
            "{}",
            1,
            OverageCapCredits: 105_000,
            OveragePricePerCredit: 0m,
            LowBalanceThresholdCredits: 140_000,
            RolloverCapCredits: 700_000,
            InvoiceTermsDays: 15,
            InvoiceGraceHours: 360);

        var result = await _planService.CreatePlanAsync(request);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
        result.Error.Should().Be(ApiMessageConstants.ValidationMessages.PlanOveragePriceRequired);
    }

    [Fact]
    public async Task GetActivePlansAsync_ShouldSeedEnterpriseWithGoogleMeetOnlyExternalIntegration()
    {
        _mockPlanRepo.Setup(r => r.FindAsync(It.IsAny<Expression<Func<Plan, bool>>>(), default))
            .ReturnsAsync(Array.Empty<Plan>());

        var result = await _planService.GetActivePlansAsync();

        result.IsSuccess.Should().BeTrue();
        _mockPlanRepo.Verify(
            r => r.AddAsync(
                It.Is<Plan>(p =>
                    p.Slug == SubscriptionConstants.PlanSlugs.Enterprise &&
                    PlanFeaturesDeclareGoogleMeetOnly(p.Features)),
                default),
            Times.Once);
    }

    [Fact]
    public async Task DeactivatePlanAsync_ShouldDeactivatePlan_WhenFound()
    {
        var planId = Guid.NewGuid();
        var plan = new Plan { Id = planId, Name = "Premium", Slug = "premium", IsActive = true };
        _mockPlanRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Plan, bool>>>(), default))
            .ReturnsAsync(plan);

        var result = await _planService.DeactivatePlanAsync(planId);

        result.IsSuccess.Should().BeTrue();
        plan.IsActive.Should().BeFalse();
        plan.DeletedAt.Should().NotBeNull();
        _mockPlanRepo.Verify(r => r.Update(plan), Times.Once);
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(default), Times.Once);
    }

    private static bool PlanFeaturesDeclareGoogleMeetOnly(string features)
    {
        using var document = JsonDocument.Parse(features);
        var root = document.RootElement;
        var integrations = root.GetProperty("external_integrations");
        var supportedPlatforms = root.GetProperty("supported_external_platforms");

        return integrations.GetProperty(SubscriptionConstants.FeatureAccess.GoogleMeetIntegration).GetBoolean() &&
               !integrations.TryGetProperty("zoom", out _) &&
               !integrations.TryGetProperty("teams", out _) &&
               supportedPlatforms.GetArrayLength() == 1 &&
               supportedPlatforms[0].GetString() == SubscriptionConstants.FeatureAccess.GoogleMeetIntegration;
    }
}
