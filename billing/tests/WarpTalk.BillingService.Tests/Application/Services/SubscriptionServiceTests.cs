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
    private readonly Mock<IWorkspaceClient> _mockWorkspaceClient;
    private readonly SubscriptionService _subscriptionService;

    public SubscriptionServiceTests()
    {
        _mockUnitOfWork = new Mock<IUnitOfWork>();
        _mockSubRepo = new Mock<ISubscriptionRepository>();
        _mockPlanRepo = new Mock<IPlanRepository>();
        _mockTxRepo = new Mock<ICreditTransactionRepository>();
        _mockStripePaymentService = new Mock<IStripePaymentService>();
        _mockWorkspaceClient = new Mock<IWorkspaceClient>();

        var mockPaymentRepo = new Mock<IPaymentRepository>();
        var mockInvoiceRepo = new Mock<IInvoiceRepository>();

        _mockUnitOfWork.Setup(u => u.SubscriptionRepository).Returns(_mockSubRepo.Object);
        _mockUnitOfWork.Setup(u => u.PlanRepository).Returns(_mockPlanRepo.Object);
        _mockUnitOfWork.Setup(u => u.CreditTransactionRepository).Returns(_mockTxRepo.Object);
        _mockUnitOfWork.Setup(u => u.PaymentRepository).Returns(mockPaymentRepo.Object);
        _mockUnitOfWork.Setup(u => u.InvoiceRepository).Returns(mockInvoiceRepo.Object);

        _subscriptionService = new SubscriptionService(
            _mockUnitOfWork.Object,
            new Mock<ILogger<SubscriptionService>>().Object,
            new Mock<IBillingMessagePublisher>().Object,
            _mockStripePaymentService.Object,
            _mockWorkspaceClient.Object);
    }

    [Fact]
    public async Task CreateSubscriptionAsync_Should_Create_Pending_Subscription()
    {
        var request = new SubscriptionRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var plan = new Plan { Id = request.PlanId, Name = "Pro", CreditsPerCycle = 1000, BillingCycle = "monthly" };

        _mockPlanRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Plan, bool>>>(), default)).ReturnsAsync(plan);
        _mockSubRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), default)).ReturnsAsync((Subscription?)null);

        var result = await _subscriptionService.CreateSubscriptionAsync(request);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Status.Should().Be("pending");
        _mockSubRepo.Verify(r => r.AddAsync(It.Is<Subscription>(s => s.Status == SubscriptionConstants.SubscriptionStatuses.Pending && !s.IsActive && s.CreditsRemaining == 0), default), Times.Once);
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(default), Times.Once);
    }

    [Fact]
    public async Task CreateSubscriptionAsync_PlanNotFound_ShouldReturnFailure()
    {
        _mockPlanRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Plan, bool>>>(), default)).ReturnsAsync((Plan?)null);

        var result = await _subscriptionService.CreateSubscriptionAsync(new SubscriptionRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));

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

        var result = await _subscriptionService.CreateSubscriptionAsync(new SubscriptionRequest(workspaceId, plan.Id, Guid.NewGuid()));

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.BillingSubscriptionAlreadyActive);
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
    public async Task ChangeSubscriptionAsync_Should_CancelOld_And_CreateNewActive()
    {
        var workspaceId = Guid.NewGuid();
        var oldPlanId = Guid.NewGuid();
        var newPlanId = Guid.NewGuid();

        var oldSub = new Subscription { Id = Guid.NewGuid(), WorkspaceId = workspaceId, PlanId = oldPlanId, IsActive = true, Status = SubscriptionConstants.SubscriptionStatuses.Active, UserId = Guid.NewGuid() };
        // Slug is required for UpdateSubscriptionAsync mock to match
        var newPlan = new Plan { Id = newPlanId, Name = "Premium", Slug = "premium", CreditsPerCycle = 5000, BillingCycle = "monthly", Currency = "usd" };

        _mockSubRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), default)).ReturnsAsync(oldSub);
        _mockPlanRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Plan, bool>>>(), default)).ReturnsAsync(newPlan);
        // SubscriptionService.ChangeSubscriptionAsync calls UpdateSubscriptionAsync with newPlan.Slug (not Name)
        _mockStripePaymentService.Setup(x => x.UpdateSubscriptionAsync(
            It.Is<UpdateStripeSubscriptionRequest>(r =>
                r.WorkspaceId == workspaceId &&
                r.NewAmount == newPlan.Price &&
                r.Currency == newPlan.Currency &&
                r.PlanSlug == newPlan.Slug),
            It.IsAny<CancellationToken>())).ReturnsAsync(Result.Success(true));

        var result = await _subscriptionService.ChangeSubscriptionAsync(new SubscriptionRequest(workspaceId, newPlanId));

        if (!result.IsSuccess)
        {
            Assert.Fail($"Test failed with error: {result.Error} (Code: {result.ErrorCode})");
        }

        result.IsSuccess.Should().BeTrue();
        // Service uses webhook-based arch: returns Pending DTO, does NOT save synchronously
        result.Value!.Status.Should().Be(SubscriptionConstants.SubscriptionStatuses.Pending);
    }

    [Fact]
    public async Task ChangeSubscriptionAsync_SamePlan_ShouldReturnFailure()
    {
        var workspaceId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        var oldSub = new Subscription { Id = Guid.NewGuid(), WorkspaceId = workspaceId, PlanId = planId, IsActive = true, Status = SubscriptionConstants.SubscriptionStatuses.Active };

        _mockSubRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), default)).ReturnsAsync(oldSub);

        var result = await _subscriptionService.ChangeSubscriptionAsync(new SubscriptionRequest(workspaceId, planId));

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.BillingSubscriptionAlreadyActive);
    }

    [Fact]
    public async Task ChangeSubscriptionAsync_NewPlanNotFound_ShouldReturnFailure()
    {
        var workspaceId = Guid.NewGuid();
        var oldSub = new Subscription { Id = Guid.NewGuid(), WorkspaceId = workspaceId, PlanId = Guid.NewGuid(), IsActive = true, Status = SubscriptionConstants.SubscriptionStatuses.Active };

        _mockSubRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), default)).ReturnsAsync(oldSub);
        _mockPlanRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Plan, bool>>>(), default)).ReturnsAsync((Plan?)null);

        var result = await _subscriptionService.ChangeSubscriptionAsync(new SubscriptionRequest(workspaceId, Guid.NewGuid()));

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.BillingPlanNotFound);
    }
}
