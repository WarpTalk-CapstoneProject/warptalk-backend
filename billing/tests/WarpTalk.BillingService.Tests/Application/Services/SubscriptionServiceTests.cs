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

public class SubscriptionManagementServiceTests
{
    private readonly Mock<IUnitOfWork> _mockUnitOfWork;
    private readonly Mock<IGenericRepository<Subscription>> _mockSubRepo;
    private readonly Mock<IGenericRepository<Plan>> _mockPlanRepo;
    private readonly Mock<IGenericRepository<CreditTransaction>> _mockTxRepo;
    private readonly Mock<WarpTalk.Shared.Protos.PaymentService.PaymentServiceClient> _mockPaymentClient;
    private readonly SubscriptionManagementService _subscriptionService;

    public SubscriptionManagementServiceTests()
    {
        _mockUnitOfWork = new Mock<IUnitOfWork>();
        _mockSubRepo = new Mock<IGenericRepository<Subscription>>();
        _mockPlanRepo = new Mock<IGenericRepository<Plan>>();
        _mockTxRepo = new Mock<IGenericRepository<CreditTransaction>>();
        _mockPaymentClient = new Mock<WarpTalk.Shared.Protos.PaymentService.PaymentServiceClient>();

        _mockUnitOfWork.Setup(u => u.SubscriptionRepository).Returns(_mockSubRepo.Object);
        _mockUnitOfWork.Setup(u => u.PlanRepository).Returns(_mockPlanRepo.Object);
        _mockUnitOfWork.Setup(u => u.CreditTransactionRepository).Returns(_mockTxRepo.Object);

        _subscriptionService = new SubscriptionManagementService(
            _mockUnitOfWork.Object,
            new Mock<ILogger<SubscriptionManagementService>>().Object,
            new Mock<IBillingMessagePublisher>().Object,
            _mockPaymentClient.Object);
    }

    // ─────────────────────────────────────────────
    //  CreateSubscriptionAsync
    // ─────────────────────────────────────────────

    [Fact]
    public async Task CreateSubscriptionAsync_Should_Create_Pending_Subscription()
    {
        var request = new CreateSubscriptionRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var plan = new Plan { Id = request.PlanId, Name = "Pro", CreditsPerCycle = 1000, BillingCycle = "monthly" };

        _mockPlanRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Plan, bool>>>(), default)).ReturnsAsync(plan);
        _mockSubRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), default)).ReturnsAsync((Subscription?)null);

        var result = await _subscriptionService.CreateSubscriptionAsync(request);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Status.Should().Be("pending");
        _mockSubRepo.Verify(r => r.AddAsync(It.Is<Subscription>(s => s.Status == "pending" && !s.IsActive && s.CreditsRemaining == 0), default), Times.Once);
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(default), Times.Once);
    }

    [Fact]
    public async Task CreateSubscriptionAsync_PlanNotFound_ShouldReturnFailure()
    {
        _mockPlanRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Plan, bool>>>(), default)).ReturnsAsync((Plan?)null);

        var result = await _subscriptionService.CreateSubscriptionAsync(new CreateSubscriptionRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.BillingPlanNotFound);
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(default), Times.Never);
    }

    [Fact]
    public async Task CreateSubscriptionAsync_WorkspaceAlreadyActive_ShouldReturnFailure()
    {
        var workspaceId = Guid.NewGuid();
        var plan = new Plan { Id = Guid.NewGuid(), Name = "Pro", CreditsPerCycle = 1000, BillingCycle = "monthly" };
        var existing = new Subscription { Id = Guid.NewGuid(), WorkspaceId = workspaceId, IsActive = true };

        _mockPlanRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Plan, bool>>>(), default)).ReturnsAsync(plan);
        _mockSubRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), default)).ReturnsAsync(existing);

        var result = await _subscriptionService.CreateSubscriptionAsync(new CreateSubscriptionRequest(Guid.NewGuid(), workspaceId, plan.Id));

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.BillingSubscriptionAlreadyActive);
    }

    // ─────────────────────────────────────────────
    //  CancelSubscriptionAsync
    // ─────────────────────────────────────────────

    [Fact]
    public async Task CancelSubscriptionAsync_Should_MarkAsCancelled_ButNotDeactivateImmediately()
    {
        var workspaceId = Guid.NewGuid();
        var subscription = new Subscription { Id = Guid.NewGuid(), Status = "active", IsActive = true };
        var plan = new Plan { Id = Guid.NewGuid(), Name = "Pro" };

        _mockSubRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), default)).ReturnsAsync(subscription);
        _mockPlanRepo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), default)).ReturnsAsync(plan);

        var result = await _subscriptionService.CancelSubscriptionAsync(workspaceId, "No longer needed");

        result.IsSuccess.Should().BeTrue();
        subscription.Status.Should().Be("cancelled");
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

    // ─────────────────────────────────────────────
    //  ChangeSubscriptionAsync
    // ─────────────────────────────────────────────

    [Fact]
    public async Task ChangeSubscriptionAsync_Should_CancelOld_And_CreateNewPending()
    {
        var workspaceId = Guid.NewGuid();
        var oldPlanId = Guid.NewGuid();
        var newPlanId = Guid.NewGuid();

        var oldSub = new Subscription { Id = Guid.NewGuid(), WorkspaceId = workspaceId, PlanId = oldPlanId, IsActive = true, Status = "active", UserId = Guid.NewGuid() };
        var newPlan = new Plan { Id = newPlanId, Name = "Premium", CreditsPerCycle = 5000, BillingCycle = "monthly", Currency = "usd" };

        _mockSubRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), default)).ReturnsAsync(oldSub);
        _mockPlanRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Plan, bool>>>(), default)).ReturnsAsync(newPlan);

        var mockCall = new Grpc.Core.AsyncUnaryCall<WarpTalk.Shared.Protos.UpdateStripeSubscriptionResponse>(
            Task.FromResult(new WarpTalk.Shared.Protos.UpdateStripeSubscriptionResponse { Success = false }),
            Task.FromResult(new Grpc.Core.Metadata()),
            () => Grpc.Core.Status.DefaultSuccess,
            () => new Grpc.Core.Metadata(),
            () => { });

        _mockPaymentClient.Setup(x => x.UpdateStripeSubscriptionAsync(
            It.IsAny<WarpTalk.Shared.Protos.UpdateStripeSubscriptionRequest>(),
            null,
            null,
            It.IsAny<CancellationToken>()))
            .Returns(mockCall);

        var result = await _subscriptionService.ChangeSubscriptionAsync(new ChangeSubscriptionRequest(workspaceId, newPlanId));

        result.IsSuccess.Should().BeTrue();
        oldSub.Status.Should().Be("cancelled");
        result.Value!.Status.Should().Be("pending");
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(default), Times.Once);
    }

    [Fact]
    public async Task ChangeSubscriptionAsync_SamePlan_ShouldReturnFailure()
    {
        var workspaceId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        var oldSub = new Subscription { Id = Guid.NewGuid(), WorkspaceId = workspaceId, PlanId = planId, IsActive = true, Status = "active" };

        _mockSubRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), default)).ReturnsAsync(oldSub);

        var result = await _subscriptionService.ChangeSubscriptionAsync(new ChangeSubscriptionRequest(workspaceId, planId));

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.BillingSubscriptionAlreadyActive);
    }

    [Fact]
    public async Task ChangeSubscriptionAsync_NewPlanNotFound_ShouldReturnFailure()
    {
        var workspaceId = Guid.NewGuid();
        var oldSub = new Subscription { Id = Guid.NewGuid(), WorkspaceId = workspaceId, PlanId = Guid.NewGuid(), IsActive = true, Status = "active" };

        _mockSubRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), default)).ReturnsAsync(oldSub);
        _mockPlanRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Plan, bool>>>(), default)).ReturnsAsync((Plan?)null);

        var result = await _subscriptionService.ChangeSubscriptionAsync(new ChangeSubscriptionRequest(workspaceId, Guid.NewGuid()));

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.BillingPlanNotFound);
    }

    // ─────────────────────────────────────────────
    //  Plan CRUD Tests
    // ─────────────────────────────────────────────

    [Fact]
    public async Task CreatePlanAsync_ShouldCreatePlan_WhenValidRequest()
    {
        var request = new CreatePlanRequest("Gold", "gold-tier", "Enterprise", 199.99m, "USD", "monthly", 1000, 10, 5, true, true, true, false, 0, true, true, "{}", 0);
        _mockPlanRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Plan, bool>>>(), default)).ReturnsAsync((Plan?)null);

        var result = await _subscriptionService.CreatePlanAsync(request);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Name.Should().Be("Gold");
        result.Value.Slug.Should().Be("gold-tier");
        _mockPlanRepo.Verify(r => r.AddAsync(It.Is<Plan>(p => p.Name == "Gold" && p.Slug == "gold-tier"), default), Times.Once);
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(default), Times.Once);
    }

    [Fact]
    public async Task CreatePlanAsync_ShouldReturnFailure_WhenDuplicateSlug()
    {
        var request = new CreatePlanRequest("Gold", "gold-tier", "Enterprise", 199.99m, "USD", "monthly", 1000, 10, 5, true, true, true, false, 0, true, true, "{}", 0);
        var existing = new Plan { Id = Guid.NewGuid(), Name = "Gold Plan", Slug = "gold-tier" };
        _mockPlanRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Plan, bool>>>(), default)).ReturnsAsync(existing);

        var result = await _subscriptionService.CreatePlanAsync(request);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("DUPLICATE_SLUG");
    }

    [Fact]
    public async Task DeactivatePlanAsync_ShouldDeactivatePlan_WhenFound()
    {
        var planId = Guid.NewGuid();
        var plan = new Plan { Id = planId, Name = "Premium", Slug = "premium", IsActive = true };
        _mockPlanRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Plan, bool>>>(), default)).ReturnsAsync(plan);
        _mockSubRepo.Setup(r => r.CountAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), default)).ReturnsAsync(0);

        var result = await _subscriptionService.DeactivatePlanAsync(planId);

        result.IsSuccess.Should().BeTrue();
        plan.IsActive.Should().BeFalse();
        plan.DeletedAt.Should().NotBeNull();
        _mockPlanRepo.Verify(r => r.Update(plan), Times.Once);
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(default), Times.Once);
    }
}
