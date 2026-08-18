using System;
using System.Linq.Expressions;
using System.Threading;
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

/// <summary>
/// A plan somebody can already buy is not a draft. WT-481.
///
/// `PUT /plans/{id}` writes all 22 columns from the request, so opening a live plan to fix a typo
/// re-sent every commercial term with it — and the admin UI's DTO carried fewer fields than the
/// entity, so the rest were written back as whatever the form defaulted to. A workspace paying
/// against that plan had its price, credits and overage economics redefined underneath it.
/// </summary>
public class PublishedPlanEditTests
{
    private readonly Mock<IPlanRepository> _mockPlanRepo = new();
    private readonly Mock<ISubscriptionRepository> _mockSubRepo = new();
    private readonly Mock<IUnitOfWork> _mockUnitOfWork = new();
    private readonly PlanService _planService;

    public PublishedPlanEditTests()
    {
        _mockUnitOfWork.Setup(u => u.Plans).Returns(_mockPlanRepo.Object);
        _mockUnitOfWork.Setup(u => u.SubscriptionRepository).Returns(_mockSubRepo.Object);

        var pricing = new Mock<IUsageRateCardAdminService>();
        pricing
            .Setup(s => s.GetPricingConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(PricingConfig));

        _planService = new PlanService(
            _mockUnitOfWork.Object,
            new Mock<ILogger<PlanService>>().Object,
            new Mock<IBillingMessagePublisher>().Object,
            pricing.Object);
    }

    private static readonly PricingConfigDto PricingConfig = new(
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

    private static readonly Guid PlanId = Guid.NewGuid();

    /// <summary>The plan as stored: published, and priced.</summary>
    private static Plan LivePlan(bool isActive = true) => new()
    {
        Id = PlanId,
        Name = "Business",
        Slug = "business",
        Tier = "business",
        Price = 49m,
        Currency = "USD",
        BillingCycle = "monthly",
        CreditsPerCycle = 50_000,
        MaxParticipants = 50,
        MaxLanguages = 3,
        Features = "[]",
        SortOrder = 2,
        IsActive = isActive,
        CreatedAt = DateTime.UtcNow.AddMonths(-3),
    };

    /// <summary>The same plan, resubmitted unchanged. Overrides are what a test is about.</summary>
    private static PlanRequest SameAsStored(
        string? name = null,
        decimal? price = null,
        int? creditsPerCycle = null,
        int? maxParticipants = null,
        bool? voiceCloneEnabled = null,
        bool? isActive = null,
        int? sortOrder = null,
        string? features = null) =>
        new(
            Name: name ?? "Business",
            Slug: "business",
            Tier: "business",
            Price: price ?? 49m,
            Currency: "USD",
            BillingCycle: "monthly",
            CreditsPerCycle: creditsPerCycle ?? 50_000,
            MaxParticipants: maxParticipants ?? 50,
            Features: features ?? "[]",
            SortOrder: sortOrder ?? 2,
            MaxLanguages: 3,
            VoiceCloneEnabled: voiceCloneEnabled ?? false,
            IsActive: isActive ?? true);

    private void GivenStoredPlan(Plan plan, bool hasSubscribers)
    {
        _mockPlanRepo
            .Setup(repo => repo.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<Plan, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(plan);
        _mockSubRepo
            .Setup(repo => repo.AnyAsync(
                It.IsAny<Expression<Func<Subscription, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(hasSubscribers);
    }

    [Fact]
    public async Task UpdatePlan_RefusesAPriceChange_OnAPublishedPlan()
    {
        GivenStoredPlan(LivePlan(), hasSubscribers: false);

        var result = await _planService.UpdatePlanAsync(PlanId, SameAsStored(price: 79m));

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.BillingPlanCommercialTermsLocked);
        result.Error.Should().Contain("price");
    }

    [Fact]
    public async Task UpdatePlan_RefusesAnEntitlementChange_OnAPublishedPlan()
    {
        GivenStoredPlan(LivePlan(), hasSubscribers: false);

        var result = await _planService.UpdatePlanAsync(
            PlanId, SameAsStored(maxParticipants: 200, voiceCloneEnabled: true));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("max participants");
        result.Error.Should().Contain("voice cloning");
    }

    [Fact]
    public async Task UpdatePlan_RefusesACreditChange_OnAHiddenPlanThatStillHasSubscribers()
    {
        // The case that would be missed by checking IsActive alone: hiding a plan does not end the
        // subscriptions running on it, and those are exactly the workspaces a silent change hits.
        GivenStoredPlan(LivePlan(isActive: false), hasSubscribers: true);

        var result = await _planService.UpdatePlanAsync(PlanId, SameAsStored(creditsPerCycle: 10, isActive: false));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("credits per cycle");
    }

    [Fact]
    public async Task UpdatePlan_AllowsTheTypoFix_OnAPublishedPlan()
    {
        // The reason this is a whitelist and not a freeze: a wrong name on a live plan should be
        // correctable without minting a replacement plan.
        GivenStoredPlan(LivePlan(), hasSubscribers: true);

        var result = await _planService.UpdatePlanAsync(
            PlanId, SameAsStored(name: "Business Plus", features: "[\"Priority support\"]", sortOrder: 3));

        result.IsSuccess.Should().BeTrue(result.Error);
    }

    [Fact]
    public async Task UpdatePlan_AllowsAPublishedPlanToBeHidden()
    {
        // Retiring a plan IS setting IsActive false. Locking this field would make a published
        // plan permanent, which is the opposite of what the ticket asks for.
        GivenStoredPlan(LivePlan(), hasSubscribers: true);

        var result = await _planService.UpdatePlanAsync(PlanId, SameAsStored(isActive: false));

        result.IsSuccess.Should().BeTrue(result.Error);
    }

    [Fact]
    public async Task UpdatePlan_AllowsEverything_OnADraftNobodyHasBought()
    {
        // Hidden and unsold is a draft, and a draft is still being written.
        GivenStoredPlan(LivePlan(isActive: false), hasSubscribers: false);

        var result = await _planService.UpdatePlanAsync(
            PlanId, SameAsStored(price: 149m, creditsPerCycle: 99_999, isActive: false));

        result.IsSuccess.Should().BeTrue(result.Error);
    }
}
