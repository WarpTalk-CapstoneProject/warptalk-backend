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
    private readonly Mock<IGenericRepository<Subscription>> _mockSubRepo;
    private readonly Mock<IGenericRepository<Plan>> _mockPlanRepo;
    private readonly SubscriptionService _subscriptionService;

    public SubscriptionServiceTests()
    {
        _mockUnitOfWork = new Mock<IUnitOfWork>();
        _mockSubRepo = new Mock<IGenericRepository<Subscription>>();
        _mockPlanRepo = new Mock<IGenericRepository<Plan>>();

        _mockUnitOfWork.Setup(u => u.SubscriptionRepository).Returns(_mockSubRepo.Object);
        _mockUnitOfWork.Setup(u => u.PlanRepository).Returns(_mockPlanRepo.Object);

        _subscriptionService = new SubscriptionService(
            _mockUnitOfWork.Object,
            new Mock<ILogger<SubscriptionService>>().Object,
            new Mock<IBillingMessagePublisher>().Object);
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

        var oldSub = new Subscription { Id = Guid.NewGuid(), WorkspaceId = workspaceId, PlanId = oldPlanId, IsActive = true, Status = "active" };
        var newPlan = new Plan { Id = newPlanId, Name = "Premium", CreditsPerCycle = 5000, BillingCycle = "monthly" };

        _mockSubRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), default)).ReturnsAsync(oldSub);
        _mockPlanRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Plan, bool>>>(), default)).ReturnsAsync(newPlan);

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
}
