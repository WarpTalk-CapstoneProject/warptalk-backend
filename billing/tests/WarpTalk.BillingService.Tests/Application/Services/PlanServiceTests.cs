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
    private readonly Mock<IGenericRepository<Plan>> _mockPlanRepo;
    private readonly Mock<ISubscriptionRepository> _mockSubRepo;
    private readonly PlanService _planService;

    public PlanServiceTests()
    {
        _mockUnitOfWork = new Mock<IUnitOfWork>();
        _mockPlanRepo = new Mock<IGenericRepository<Plan>>();
        _mockSubRepo = new Mock<ISubscriptionRepository>();

        _mockUnitOfWork.Setup(u => u.Plans).Returns(_mockPlanRepo.Object);
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
