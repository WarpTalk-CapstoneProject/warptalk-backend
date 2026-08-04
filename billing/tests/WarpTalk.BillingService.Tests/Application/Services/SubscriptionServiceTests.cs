using WarpTalk.BillingService.Domain.Constants;
using System;
using System.Collections.Generic;
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

public class SubscriptionServiceTests
{
    private readonly Mock<IUnitOfWork> _mockUnitOfWork;
    private readonly Mock<ISubscriptionRepository> _mockSubRepo;
    private readonly Mock<IPlanRepository> _mockPlanRepo;
    private readonly Mock<ICreditTransactionRepository> _mockTxRepo;
    private readonly Mock<IStripePaymentService> _mockStripePaymentService;
    private readonly Mock<IAiServiceStateStore> _mockAiServiceStateStore;
    private readonly SubscriptionService _subscriptionService;

    public SubscriptionServiceTests()
    {
        _mockUnitOfWork = new Mock<IUnitOfWork>();
        _mockSubRepo = new Mock<ISubscriptionRepository>();
        _mockPlanRepo = new Mock<IPlanRepository>();
        _mockTxRepo = new Mock<ICreditTransactionRepository>();
        _mockStripePaymentService = new Mock<IStripePaymentService>();
        _mockAiServiceStateStore = new Mock<IAiServiceStateStore>();

        var mockPaymentRepo = new Mock<IPaymentRepository>();
        var mockInvoiceRepo = new Mock<IInvoiceRepository>();

        _mockUnitOfWork.Setup(u => u.SubscriptionRepository).Returns(_mockSubRepo.Object);
        _mockUnitOfWork.Setup(u => u.Plans).Returns(_mockPlanRepo.Object);
        _mockUnitOfWork.Setup(u => u.CreditTransactionRepository).Returns(_mockTxRepo.Object);
        _mockUnitOfWork.Setup(u => u.PaymentRepository).Returns(mockPaymentRepo.Object);
        _mockUnitOfWork.Setup(u => u.InvoiceRepository).Returns(mockInvoiceRepo.Object);

        _subscriptionService = new SubscriptionService(
            _mockUnitOfWork.Object,
            new Mock<ILogger<SubscriptionService>>().Object,
            new Mock<IBillingMessagePublisher>().Object,
            _mockStripePaymentService.Object,
            CreatePricingConfigService(),
            new Mock<IWorkspaceClient>().Object,
            _mockAiServiceStateStore.Object);
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

    [Fact]
    public async Task CreateWorkspaceContractSubscriptionAsync_Should_Create_Active_Subscription()
    {
        var request = new CreateWorkspaceContractSubscriptionRequest(
            WorkspaceId: Guid.NewGuid(),
            PlanId: Guid.NewGuid(),
            ContractTerms: new UpdateSubscriptionContractTermsRequest(
                CreditsPerCycleOverride: 710_000,
                ContractPriceVnd: 1_900_000m,
                OverageCapCreditsOverride: 105_000,
                OveragePricePerCreditOverride: 4m,
                InvoiceTermsDaysOverride: 15,
                BillingContactEmail: "billing@example.com"),
            UserId: Guid.NewGuid());
        var plan = new Plan
        {
            Id = request.PlanId,
            Name = "Enterprise",
            Price = 1_900_000m,
            CreditsPerCycle = 700_000,
            OverageCapCredits = 105_000,
            OveragePricePerCredit = 4m,
            InvoiceTermsDays = 15
        };

        _mockPlanRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Plan, bool>>>(), default)).ReturnsAsync(plan);
        _mockSubRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), default)).ReturnsAsync((Subscription?)null);

        var result = await _subscriptionService.CreateWorkspaceContractSubscriptionAsync(request);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Status.Should().Be(SubscriptionConstants.SubscriptionStatuses.Active);
        result.Value.CreditsRemaining.Should().Be(710_000);
        result.Value.ContractPriceVnd.Should().Be(1_900_000m);
        result.Value.BillingContactEmail.Should().Be("billing@example.com");
        _mockSubRepo.Verify(r => r.AddAsync(It.Is<Subscription>(s =>
            s.Status == SubscriptionConstants.SubscriptionStatuses.Active &&
            s.IsActive &&
            s.CreditsRemaining == 710_000 &&
            s.ContractPriceVnd == 1_900_000m), default), Times.Once);
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(default), Times.Once);
    }

    [Fact]
    public async Task CreateWorkspaceContractSubscriptionAsync_Should_Fallback_To_Plan_Price_If_Not_Provided()
    {
        var request = new CreateWorkspaceContractSubscriptionRequest(
            WorkspaceId: Guid.NewGuid(),
            PlanId: Guid.NewGuid(),
            ContractTerms: new UpdateSubscriptionContractTermsRequest(
                CreditsPerCycleOverride: null,
                ContractPriceVnd: null,
                OverageCapCreditsOverride: null,
                OveragePricePerCreditOverride: null,
                InvoiceTermsDaysOverride: null,
                BillingContactEmail: null),
            UserId: Guid.NewGuid());
        var plan = new Plan
        {
            Id = request.PlanId,
            Name = "Enterprise",
            Price = 2_500_000m,
            CreditsPerCycle = 800_000,
            OverageCapCredits = 100_000,
            OveragePricePerCredit = 5m,
        };

        _mockPlanRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Plan, bool>>>(), default)).ReturnsAsync(plan);
        _mockSubRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), default)).ReturnsAsync((Subscription?)null);

        var result = await _subscriptionService.CreateWorkspaceContractSubscriptionAsync(request);

        result.IsSuccess.Should().BeTrue();
        // ContractPriceVnd override is null (not provided) -- correct
        result.Value!.ContractPriceVnd.Should().BeNull();
        // But the EFFECTIVE price must fallback to plan.Price
        result.Value.EffectiveContractPriceVnd.Should().Be(2_500_000m);
        result.Value.CreditsRemaining.Should().Be(800_000);

        _mockSubRepo.Verify(r => r.AddAsync(It.Is<Subscription>(s =>
            s.ContractPriceVnd == null && s.CreditsRemaining == 800_000), default), Times.Once);
    }

    [Fact]
    public async Task CancelSubscriptionAsync_Should_MarkAsCancelled_ButNotDeactivateImmediately()
    {
        var workspaceId = Guid.NewGuid();
        var subscription = new Subscription { Id = Guid.NewGuid(), Status = SubscriptionConstants.SubscriptionStatuses.Active, IsActive = true };
        var plan = new Plan { Id = Guid.NewGuid(), Name = "Pro" };

        _mockSubRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), default)).ReturnsAsync(subscription);
        _mockPlanRepo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), default)).ReturnsAsync(plan);

        var result = await _subscriptionService.CancelSubscriptionAsync(workspaceId, "No longer needed");

        result.IsSuccess.Should().BeTrue();
        subscription.Status.Should().Be(SubscriptionConstants.SubscriptionStatuses.Cancelled);
        subscription.IsActive.Should().BeTrue(); // Still has access until period_end
        _mockSubRepo.Verify(r => r.Update(subscription), Times.Once);
    }

    [Fact]
    public async Task CancelSubscriptionAsync_SubscriptionNotFound_ShouldReturnFailure()
    {
        _mockSubRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), default)).ReturnsAsync((Subscription?)null);

        var result = await _subscriptionService.CancelSubscriptionAsync(Guid.NewGuid(), null);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.BillingSubscriptionNotFound);
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(default), Times.Never);
    }

    [Fact]
    public async Task ResumeSubscriptionAsync_Should_Clear_Suspend_State_And_Sync_Redis()
    {
        var workspaceId = Guid.NewGuid();
        var plan = new Plan { Id = Guid.NewGuid(), Name = "Enterprise", Price = 1_000_000m };
        var subscription = new Subscription
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            PlanId = plan.Id,
            IsActive = true,
            ServiceState = SubscriptionConstants.ServiceStates.Suspended,
            SuspendedReason = SubscriptionConstants.SuspendedReasons.InvoiceOverdue,
            OverageStartedAt = DateTime.UtcNow.AddDays(-1)
        };

        _mockSubRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), default)).ReturnsAsync(subscription);
        _mockPlanRepo.Setup(r => r.GetByIdAsync(plan.Id, default)).ReturnsAsync(plan);
        _mockAiServiceStateStore
            .Setup(r => r.SetAiServiceStateAsync(workspaceId, SubscriptionConstants.ServiceStates.Healthy, null, default))
            .ReturnsAsync(Result.Success());

        var result = await _subscriptionService.ResumeSubscriptionAsync(workspaceId, new ResumeSubscriptionRequest("paid overdue invoice"));

        result.IsSuccess.Should().BeTrue();
        subscription.ServiceState.Should().Be(SubscriptionConstants.ServiceStates.Healthy);
        subscription.SuspendedReason.Should().BeNull();
        subscription.OverageStartedAt.Should().BeNull();
        _mockSubRepo.Verify(r => r.Update(subscription), Times.Once);
        _mockAiServiceStateStore.Verify(r => r.SetAiServiceStateAsync(workspaceId, SubscriptionConstants.ServiceStates.Healthy, null, default), Times.Once);
    }

    [Fact]
    public async Task ResumeSubscriptionAsync_When_Not_Suspended_Should_Return_Conflict()
    {
        var workspaceId = Guid.NewGuid();
        var subscription = new Subscription
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            IsActive = true,
            ServiceState = SubscriptionConstants.ServiceStates.Healthy
        };

        _mockSubRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), default)).ReturnsAsync(subscription);

        var result = await _subscriptionService.ResumeSubscriptionAsync(workspaceId, new ResumeSubscriptionRequest());

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.BillingSubscriptionConflict);
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(default), Times.Never);
    }

    [Fact]
    public async Task UpdateContractTermsAsync_Should_Update_Overrides()
    {
        var workspaceId = Guid.NewGuid();
        var plan = new Plan
        {
            Id = Guid.NewGuid(),
            Name = "Enterprise",
            Price = 1_900_000m,
            CreditsPerCycle = 700_000,
            OverageCapCredits = 105_000,
            OveragePricePerCredit = 4m,
            InvoiceTermsDays = 15
        };
        var subscription = new Subscription
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            PlanId = plan.Id,
            UserId = Guid.NewGuid(),
            IsActive = true
        };
        var request = new UpdateSubscriptionContractTermsRequest(
            CreditsPerCycleOverride: 800_000,
            ContractPriceVnd: 2_400_000m,
            OverageCapCreditsOverride: 120_000,
            OveragePricePerCreditOverride: 4.5m,
            InvoiceTermsDaysOverride: 30,
            BillingContactEmail: " billing@example.com ");

        _mockSubRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), default)).ReturnsAsync(subscription);
        _mockPlanRepo.Setup(r => r.GetByIdAsync(plan.Id, default)).ReturnsAsync(plan);

        var result = await _subscriptionService.UpdateContractTermsAsync(workspaceId, request);

        result.IsSuccess.Should().BeTrue();
        subscription.CreditsPerCycleOverride.Should().Be(800_000);
        subscription.ContractPriceVnd.Should().Be(2_400_000m);
        subscription.OverageCapCreditsOverride.Should().Be(120_000);
        subscription.OveragePricePerCreditOverride.Should().Be(4.5m);
        subscription.InvoiceTermsDaysOverride.Should().Be(30);
        subscription.BillingContactEmail.Should().Be("billing@example.com");
        result.Value!.EffectiveCreditsPerCycle.Should().Be(800_000);
        result.Value.EffectiveContractPriceVnd.Should().Be(2_400_000m);
        _mockSubRepo.Verify(r => r.Update(subscription), Times.Once);
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(default), Times.Once);
    }

    [Fact]
    public async Task UpdateContractTermsAsync_Should_Block_Price_Below_Floor()
    {
        var workspaceId = Guid.NewGuid();
        var plan = new Plan
        {
            Id = Guid.NewGuid(),
            Price = 1_900_000m,
            CreditsPerCycle = 700_000
        };
        var subscription = new Subscription
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            PlanId = plan.Id,
            IsActive = true
        };

        _mockSubRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), default)).ReturnsAsync(subscription);
        _mockPlanRepo.Setup(r => r.GetByIdAsync(plan.Id, default)).ReturnsAsync(plan);

        var result = await _subscriptionService.UpdateContractTermsAsync(
            workspaceId,
            new UpdateSubscriptionContractTermsRequest(
                CreditsPerCycleOverride: 1_000_000,
                ContractPriceVnd: 2_000_000m));

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
        result.Error.Should().Be(BillingMessageConstants.ApiErrorMessages.BillingContractPriceBelowFloor);
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(default), Times.Never);
    }

    [Fact]
    public async Task UpdateContractTermsAsync_Should_Block_Invalid_Billing_Email()
    {
        var workspaceId = Guid.NewGuid();
        var plan = new Plan
        {
            Id = Guid.NewGuid(),
            Price = 1_900_000m,
            CreditsPerCycle = 700_000,
            OverageCapCredits = 105_000,
            OveragePricePerCredit = 4m
        };
        var subscription = new Subscription
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            PlanId = plan.Id,
            IsActive = true
        };

        _mockSubRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), default)).ReturnsAsync(subscription);
        _mockPlanRepo.Setup(r => r.GetByIdAsync(plan.Id, default)).ReturnsAsync(plan);

        var result = await _subscriptionService.UpdateContractTermsAsync(
            workspaceId,
            new UpdateSubscriptionContractTermsRequest(BillingContactEmail: "not-an-email"));

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(default), Times.Never);
    }

    [Fact]
    public async Task UpdateContractTermsAsync_Should_Block_Invalid_Overage_Terms()
    {
        var workspaceId = Guid.NewGuid();
        var plan = new Plan
        {
            Id = Guid.NewGuid(),
            Price = 1_900_000m,
            CreditsPerCycle = 700_000,
            OverageCapCredits = 105_000,
            OveragePricePerCredit = 4m
        };
        var subscription = new Subscription
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            PlanId = plan.Id,
            IsActive = true
        };

        _mockSubRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), default)).ReturnsAsync(subscription);
        _mockPlanRepo.Setup(r => r.GetByIdAsync(plan.Id, default)).ReturnsAsync(plan);

        var capResult = await _subscriptionService.UpdateContractTermsAsync(
            workspaceId,
            new UpdateSubscriptionContractTermsRequest(
                CreditsPerCycleOverride: 700_000,
                ContractPriceVnd: 1_900_000m,
                OverageCapCreditsOverride: 800_000,
                OveragePricePerCreditOverride: 4m));

        var priceResult = await _subscriptionService.UpdateContractTermsAsync(
            workspaceId,
            new UpdateSubscriptionContractTermsRequest(
                CreditsPerCycleOverride: 700_000,
                ContractPriceVnd: 1_900_000m,
                OverageCapCreditsOverride: 105_000,
                OveragePricePerCreditOverride: 3m));

        capResult.IsSuccess.Should().BeFalse();
        capResult.ErrorCode.Should().Be(ErrorCodes.ValidationError);
        capResult.Error.Should().Be(BillingMessageConstants.ApiErrorMessages.BillingContractOverageTermsInvalid);
        priceResult.IsSuccess.Should().BeFalse();
        priceResult.ErrorCode.Should().Be(ErrorCodes.ValidationError);
        priceResult.Error.Should().Be(BillingMessageConstants.ApiErrorMessages.BillingContractOverageTermsInvalid);
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(default), Times.Never);
    }

    [Fact]
    public async Task UpdateContractTermsAsync_Should_Block_Reducing_Commitment_During_Overage()
    {
        var workspaceId = Guid.NewGuid();
        var plan = new Plan
        {
            Id = Guid.NewGuid(),
            Price = 1_900_000m,
            CreditsPerCycle = 700_000,
            OverageCapCredits = 105_000
        };
        var subscription = new Subscription
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            PlanId = plan.Id,
            IsActive = true,
            OverageStartedAt = DateTime.UtcNow.AddDays(-1),
            OverageCreditsThisCycle = 20_000
        };

        _mockSubRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), default)).ReturnsAsync(subscription);
        _mockPlanRepo.Setup(r => r.GetByIdAsync(plan.Id, default)).ReturnsAsync(plan);

        var result = await _subscriptionService.UpdateContractTermsAsync(
            workspaceId,
            new UpdateSubscriptionContractTermsRequest(
                CreditsPerCycleOverride: 600_000,
                ContractPriceVnd: 1_800_000m));

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.BillingSubscriptionConflict);
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(default), Times.Never);
    }


    // ========================================================================
    // Contract Price Invariant: Đổi plans.price không ảnh hưởng hợp đồng đã ký
    // ========================================================================

    [Fact]
    public async Task UpdateContractTerms_ChangingPlanPrice_DoesNotAffectExistingContractPrice()
    {
        // Arrange: existing contract has ContractPriceVnd locked at 1,900,000
        var workspaceId = Guid.NewGuid();
        var plan = new Plan { Id = Guid.NewGuid(), Name = "Enterprise", Price = 1_900_000m, CreditsPerCycle = 700_000, OverageCapCredits = 100_000, OveragePricePerCredit = 4m };
        var subscription = new Subscription
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            PlanId = plan.Id,
            IsActive = true,
            Status = SubscriptionConstants.SubscriptionStatuses.Active,
            CreditsRemaining = 700_000,
            ContractPriceVnd = 1_900_000m, // Locked at signing time
            CreditsPerCycleOverride = 700_000,
        };

        _mockSubRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), default)).ReturnsAsync(subscription);
        _mockPlanRepo.Setup(r => r.GetByIdAsync(plan.Id, default))
            // Simulate plan.Price was bumped to 2,200,000 after contract signed
            .ReturnsAsync(new Plan { Id = plan.Id, Name = plan.Name, Price = 2_200_000m, CreditsPerCycle = 700_000, OverageCapCredits = 100_000, OveragePricePerCredit = 4m });

        // Act: update contract terms without changing price
        var result = await _subscriptionService.UpdateContractTermsAsync(workspaceId,
            new UpdateSubscriptionContractTermsRequest(ContractPriceVnd: 1_900_000m));

        // Assert: success, and entity still has the original locked price
        result.IsSuccess.Should().BeTrue();
        // The subscription entity's ContractPriceVnd is the field that matters for invoice generation
        subscription.ContractPriceVnd.Should().Be(1_900_000m, "contract price is locked at signing, plan.Price bump should not override it");
    }

    // ========================================================================
    // Sàn giá: contract_price / credits < 2.60 → DB từ chối
    // ========================================================================

    [Fact]
    public async Task CreateContractSubscription_PriceBelowFloor_ShouldReturnValidationError()
    {
        // Floor = 2.60 VND/credit. 500_000 credits at 1,200,000 VND = 2.40 VND/credit < floor
        var request = new CreateWorkspaceContractSubscriptionRequest(
            WorkspaceId: Guid.NewGuid(),
            PlanId: Guid.NewGuid(),
            ContractTerms: new UpdateSubscriptionContractTermsRequest(
                CreditsPerCycleOverride: 500_000,
                ContractPriceVnd: 1_200_000m), // 2.40 VND/credit < 2.60 floor
            UserId: Guid.NewGuid());
        var plan = new Plan { Id = request.PlanId, Name = "Enterprise", Price = 2_000_000m, CreditsPerCycle = 700_000, OverageCapCredits = 100_000, OveragePricePerCredit = 4m };

        _mockPlanRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Plan, bool>>>(), default)).ReturnsAsync(plan);
        _mockSubRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), default)).ReturnsAsync((Subscription?)null);

        var result = await _subscriptionService.CreateWorkspaceContractSubscriptionAsync(request);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(default), Times.Never);
    }

    [Fact]
    public async Task CreateContractSubscription_PriceAtOrAboveFloor_ShouldSucceed()
    {
        // Exactly at floor: 500_000 credits * 2.60 = 1,300,000 VND
        var request = new CreateWorkspaceContractSubscriptionRequest(
            WorkspaceId: Guid.NewGuid(),
            PlanId: Guid.NewGuid(),
            ContractTerms: new UpdateSubscriptionContractTermsRequest(
                CreditsPerCycleOverride: 500_000,
                ContractPriceVnd: 1_300_000m), // Exactly 2.60 VND/credit
            UserId: Guid.NewGuid());
        var plan = new Plan { Id = request.PlanId, Name = "Enterprise", Price = 2_000_000m, CreditsPerCycle = 700_000, OverageCapCredits = 100_000, OveragePricePerCredit = 4m };

        _mockPlanRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Plan, bool>>>(), default)).ReturnsAsync(plan);
        _mockSubRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), default)).ReturnsAsync((Subscription?)null);

        var result = await _subscriptionService.CreateWorkspaceContractSubscriptionAsync(request);

        result.IsSuccess.Should().BeTrue("price is exactly at the floor");
    }

    // ========================================================================
    // Trial subscription tests
    // ========================================================================

    [Fact]
    public async Task CreateTrialSubscriptionAsync_Should_Grant_20000_Credits_And_NoOverage()
    {
        var request = new TrialSubscriptionRequest(
            WorkspaceId: Guid.NewGuid(),
            UserId: Guid.NewGuid(),
            OwnerEmail: "ceo@acme.com");

        var plan = new Plan { Id = Guid.NewGuid(), Name = "Enterprise", Price = 2_000_000m, CreditsPerCycle = 700_000, OverageCapCredits = 100_000, Slug = "enterprise", IsActive = true };

        _mockPlanRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Plan, bool>>>(), default)).ReturnsAsync(plan);
        // No existing active subscription
        _mockSubRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), default)).ReturnsAsync((Subscription?)null);

        var result = await _subscriptionService.CreateTrialSubscriptionAsync(request);

        result.IsSuccess.Should().BeTrue();
        result.Value!.CreditsRemaining.Should().Be(20_000, "trial always grants 20,000 credits");
        // TrialEndsAt should be set
        result.Value.TrialEndsAt.Should().NotBeNull("trial must have an expiry");
        // Overage should be 0 (cap = 0 → can't go negative)
        _mockSubRepo.Verify(r => r.AddAsync(It.Is<Subscription>(s =>
            s.CreditsRemaining == 20_000 &&
            s.OverageCapCreditsOverride == 0 &&
            s.TrialEndsAt != null &&
            s.OwnerEmailDomain == "acme.com"), default), Times.Once);
    }

    [Fact]
    public async Task CreateTrialSubscriptionAsync_Should_Block_Duplicate_Domain()
    {
        // First trial for acme.com already exists
        var existingTrial = new Subscription
        {
            Id = Guid.NewGuid(),
            WorkspaceId = Guid.NewGuid(),
            OwnerEmailDomain = "acme.com",
            TrialEndsAt = DateTime.UtcNow.AddDays(7),
        };

        var request = new TrialSubscriptionRequest(
            WorkspaceId: Guid.NewGuid(), // Different workspace
            UserId: Guid.NewGuid(),
            OwnerEmail: "cfo@acme.com");  // Same domain!

        var plan = new Plan { Id = Guid.NewGuid(), Name = "Enterprise", Slug = "enterprise", IsActive = true, Price = 2_000_000m };
        _mockPlanRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Plan, bool>>>(), default)).ReturnsAsync(plan);

        // First call (check active subscription) → null, second call (check domain) → existing trial
        var callCount = 0;
        _mockSubRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), default))
            .ReturnsAsync(() => callCount++ == 0 ? null : existingTrial);

        var result = await _subscriptionService.CreateTrialSubscriptionAsync(request);

        result.IsSuccess.Should().BeFalse("duplicate trial for same email domain must be rejected");
        result.ErrorCode.Should().Be(ErrorCodes.BillingSubscriptionConflict);
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(default), Times.Never);
    }
    [Fact]
    public async Task UpdateContractTerms_Should_AutoResume_AI_When_OverageCap_Is_Increased()
    {
        var workspaceId = Guid.NewGuid();
        var plan = new Plan { Id = Guid.NewGuid(), Name = "Enterprise", Price = 2_000_000m, CreditsPerCycle = 700_000, OverageCapCredits = 100_000, OveragePricePerCredit = 4m };
        var subscription = new Subscription
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            PlanId = plan.Id,
            IsActive = true,
            Status = SubscriptionConstants.SubscriptionStatuses.Active,
            CreditsRemaining = 0,
            OverageCreditsThisCycle = 150_000, // over the cap
            OverageCapCreditsOverride = 100_000,
            ServiceState = SubscriptionConstants.ServiceStates.Suspended,
            SuspendedReason = SubscriptionConstants.SuspendedReasons.OverageCap
        };

        _mockSubRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), default)).ReturnsAsync(subscription);
        _mockPlanRepo.Setup(r => r.GetByIdAsync(plan.Id, default)).ReturnsAsync(plan);
        _mockAiServiceStateStore.Setup(s => s.SetAiServiceStateAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), default))
            .ReturnsAsync(Result.Success());

        // Increase cap to 200k, resolving the overage
        var result = await _subscriptionService.UpdateContractTermsAsync(workspaceId,
            new UpdateSubscriptionContractTermsRequest(OverageCapCreditsOverride: 200_000));

        result.IsSuccess.Should().BeTrue();
        subscription.ServiceState.Should().Be(SubscriptionConstants.ServiceStates.Healthy);
        subscription.SuspendedReason.Should().BeNull();

        _mockAiServiceStateStore.Verify(s => s.SetAiServiceStateAsync(workspaceId, SubscriptionConstants.ServiceStates.Healthy, null, default), Times.Once);
    }
}
