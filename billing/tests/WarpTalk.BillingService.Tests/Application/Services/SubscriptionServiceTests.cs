using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using FluentAssertions;
using Moq;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Domain.Entities;
using System.Linq.Expressions;
using System.Collections.Generic;

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

        _subscriptionService = new SubscriptionService(_mockUnitOfWork.Object, new Mock<ILogger<SubscriptionService>>().Object, new Mock<IBillingMessagePublisher>().Object);
    }

    [Fact]
    public async Task CreateSubscriptionAsync_Should_Create_Pending_Subscription()
    {
        var request = new CreateSubscriptionRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var plan = new Plan { Id = request.PlanId, Name = "Pro", CreditsPerCycle = 1000, BillingCycle = "monthly" };

        _mockPlanRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Plan, bool>>>(), default)).ReturnsAsync(plan);
        _mockSubRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), default)).ReturnsAsync((Subscription)null);

        var result = await _subscriptionService.CreateSubscriptionAsync(request);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be("pending");
        _mockSubRepo.Verify(r => r.AddAsync(It.Is<Subscription>(s => s.Status == "pending" && !s.IsActive && s.CreditsRemaining == 0), default), Times.Once);
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(default), Times.Once);
    }

    [Fact]
    public async Task CancelSubscriptionAsync_Should_MarkAsCancelled_ButNotDeactivateImmediately()
    {
        var subscriptionId = Guid.NewGuid();
        var subscription = new Subscription { Id = subscriptionId, Status = "active", IsActive = true };

        _mockSubRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), default)).ReturnsAsync(subscription);

        var result = await _subscriptionService.CancelSubscriptionAsync(subscriptionId, "No longer needed");

        result.IsSuccess.Should().BeTrue();
        subscription.Status.Should().Be("cancelled");
        subscription.IsActive.Should().BeTrue(); // Still has access until expired!
        _mockSubRepo.Verify(r => r.Update(subscription), Times.Once);
    }
}
