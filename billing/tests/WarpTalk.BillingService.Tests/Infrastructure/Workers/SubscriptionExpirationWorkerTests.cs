using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Options;
using WarpTalk.BillingService.Infrastructure.Workers;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Tests.Infrastructure.Workers;

public class SubscriptionExpirationWorkerTests
{
    [Fact]
    public async Task SweepAsync_Should_Suspend_Expired_Trial_Without_Deactivating_Subscription()
    {
        var subscription = new Subscription
        {
            Id = Guid.NewGuid(),
            WorkspaceId = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            Status = SubscriptionConstants.SubscriptionStatuses.Active,
            ServiceState = SubscriptionConstants.ServiceStates.Healthy,
            SuspendedReason = null,
            IsActive = true,
            TrialEndsAt = DateTime.UtcNow.AddDays(-1),
            CurrentPeriodEnd = DateTime.UtcNow.AddDays(-1)
        };

        var subscriptionRepository = new Mock<ISubscriptionRepository>();
        subscriptionRepository
            .Setup(r => r.GetExpiredActiveSubscriptionsAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { subscription });

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(u => u.SubscriptionRepository).Returns(subscriptionRepository.Object);
        unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var aiServiceStateStore = new Mock<IAiServiceStateStore>();
        aiServiceStateStore
            .Setup(s => s.SetAiServiceStateAsync(
                subscription.WorkspaceId,
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        var services = new ServiceCollection()
            .AddSingleton(unitOfWork.Object)
            .AddSingleton(aiServiceStateStore.Object)
            .BuildServiceProvider();

        var worker = new SubscriptionExpirationWorker(
            services,
            Mock.Of<ILogger<SubscriptionExpirationWorker>>(),
            Options.Create(new BillingWorkerOptions()));

        await worker.SweepAsync(CancellationToken.None);

        subscription.IsActive.Should().BeTrue();
        subscription.Status.Should().Be(SubscriptionConstants.SubscriptionStatuses.Active);
        subscription.ServiceState.Should().Be(SubscriptionConstants.ServiceStates.Suspended);
        subscription.SuspendedReason.Should().Be(SubscriptionConstants.SuspendedReasons.TrialEnded);
        subscriptionRepository.Verify(r => r.Update(subscription), Times.Once);
        aiServiceStateStore.Verify(s => s.SetAiServiceStateAsync(
            subscription.WorkspaceId,
            SubscriptionConstants.ServiceStates.Suspended,
            SubscriptionConstants.SuspendedReasons.TrialEnded,
            It.IsAny<CancellationToken>()), Times.Once);
        unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SweepAsync_Should_Expire_NonTrial_Subscription()
    {
        var subscription = new Subscription
        {
            Id = Guid.NewGuid(),
            WorkspaceId = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            Status = SubscriptionConstants.SubscriptionStatuses.Active,
            ServiceState = SubscriptionConstants.ServiceStates.Healthy,
            IsActive = true,
            TrialEndsAt = null,
            CurrentPeriodEnd = DateTime.UtcNow.AddDays(-1)
        };

        var subscriptionRepository = new Mock<ISubscriptionRepository>();
        subscriptionRepository
            .Setup(r => r.GetExpiredActiveSubscriptionsAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { subscription });

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(u => u.SubscriptionRepository).Returns(subscriptionRepository.Object);
        unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var services = new ServiceCollection()
            .AddSingleton(unitOfWork.Object)
            .BuildServiceProvider();

        var worker = new SubscriptionExpirationWorker(
            services,
            Mock.Of<ILogger<SubscriptionExpirationWorker>>(),
            Options.Create(new BillingWorkerOptions()));

        await worker.SweepAsync(CancellationToken.None);

        subscription.IsActive.Should().BeFalse();
        subscription.Status.Should().Be(SubscriptionConstants.SubscriptionStatuses.Expired);
        subscription.ServiceState.Should().Be(SubscriptionConstants.ServiceStates.Healthy);
        subscriptionRepository.Verify(r => r.Update(subscription), Times.Once);
        unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
