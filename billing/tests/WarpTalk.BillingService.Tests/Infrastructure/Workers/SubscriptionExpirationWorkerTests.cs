using WarpTalk.Shared.Coordination;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using WarpTalk.BillingService.Application.Entitlements;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Services;
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
            .Setup(r => r.GetExpiredActiveSubscriptionsAsync(It.IsAny<DateTime>(), It.IsAny<TimeSpan>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
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
            Options.Create(new BillingWorkerOptions()),
            new DistributedLockProvider(new InProcessLeaseStore(TimeProvider.System), TimeProvider.System));

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
            .Setup(r => r.GetExpiredActiveSubscriptionsAsync(It.IsAny<DateTime>(), It.IsAny<TimeSpan>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
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
            Options.Create(new BillingWorkerOptions()),
            new DistributedLockProvider(new InProcessLeaseStore(TimeProvider.System), TimeProvider.System));

        await worker.SweepAsync(CancellationToken.None);

        subscription.IsActive.Should().BeFalse();
        subscription.Status.Should().Be(SubscriptionConstants.SubscriptionStatuses.Expired);
        subscription.ServiceState.Should().Be(SubscriptionConstants.ServiceStates.Healthy);
        subscriptionRepository.Verify(r => r.Update(subscription), Times.Once);
        unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// THE LEAK. Expiring a subscription was a row update and nothing else: the entitlement snapshot
    /// kept has_active_subscription = true (WT-515 let the workspace create rooms) and the Redis flag
    /// Start Translation reads was never written. A workspace expired on 23 Sep translated free on
    /// 24 Sep. Both signals now go out with the expiry.
    /// </summary>
    [Fact]
    public async Task SweepAsync_Should_Tell_The_Paywall_And_The_Ai_Pipeline_That_The_Subscription_Expired()
    {
        var subscription = new Subscription
        {
            Id = Guid.NewGuid(),
            WorkspaceId = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            Status = SubscriptionConstants.SubscriptionStatuses.Active,
            ServiceState = SubscriptionConstants.ServiceStates.Healthy,
            IsActive = true,
            CurrentPeriodEnd = DateTime.UtcNow.AddDays(-1)
        };

        var subscriptionRepository = new Mock<ISubscriptionRepository>();
        subscriptionRepository
            .Setup(r => r.GetExpiredActiveSubscriptionsAsync(It.IsAny<DateTime>(), It.IsAny<TimeSpan>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { subscription });

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(u => u.SubscriptionRepository).Returns(subscriptionRepository.Object);
        unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var aiServiceStateStore = new Mock<IAiServiceStateStore>();
        aiServiceStateStore
            .Setup(s => s.SetAiServiceStateAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        var entitlements = new Mock<IEntitlementChangePublisher>();
        var freezer = new Mock<ICreditFreezeService>();
        var settings = new Mock<WarpTalk.Shared.PlatformSettings.IPlatformSettings>();
        settings
            .Setup(p => p.GetInt32Async(
                "billing.frozen_credits.grace_days",
                It.IsAny<int?>(),
                It.IsAny<WarpTalk.Shared.PlatformSettings.SettingContext>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(7);

        var services = new ServiceCollection()
            .AddSingleton(unitOfWork.Object)
            .AddSingleton(aiServiceStateStore.Object)
            .AddSingleton(entitlements.Object)
            .AddSingleton(freezer.Object)
            .AddSingleton(settings.Object)
            .BuildServiceProvider();

        var worker = new SubscriptionExpirationWorker(
            services,
            Mock.Of<ILogger<SubscriptionExpirationWorker>>(),
            Options.Create(new BillingWorkerOptions()),
            new DistributedLockProvider(new InProcessLeaseStore(TimeProvider.System), TimeProvider.System));

        await worker.SweepAsync(CancellationToken.None);

        aiServiceStateStore.Verify(s => s.SetAiServiceStateAsync(
            subscription.WorkspaceId,
            SubscriptionConstants.ServiceStates.Suspended,
            SubscriptionConstants.SuspendedReasons.SubscriptionExpired,
            It.IsAny<CancellationToken>()), Times.Once);
        entitlements.Verify(p => p.EnqueueAsync(
            subscription.WorkspaceId,
            EntitlementConstants.Reasons.SubscriptionExpired,
            It.IsAny<CancellationToken>()), Times.Once);
        // Then the ended balance is frozen / forfeited, released and aged, in that order.
        freezer.Verify(f => f.SplitEndedSubscriptionsAsync(It.IsAny<DateTime>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Once);
        freezer.Verify(f => f.ReleaseFrozenCreditsAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        // The grace window is the live platform setting, not a constant.
        freezer.Verify(f => f.MarkDormantAsync(It.IsAny<DateTime>(), 7, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// The grandfathering boundary: an operator's platform setting wins; otherwise the instant the
    /// migration recorded; with neither, null — which grandfathers everything.
    /// </summary>
    [Theory]
    [InlineData("2026-10-01T00:00:00Z", 1_790_000_000.0, "2026-10-01T00:00:00Z")]
    [InlineData("", 1_790_000_000.0, "2026-09-21T14:13:20Z")]
    [InlineData("", -1.0, null)]
    public async Task The_policy_boundary_is_the_setting_then_the_migration_time_then_unknown(
        string configured, double recordedEpoch, string? expected)
    {
        var settings = new Mock<WarpTalk.Shared.PlatformSettings.IPlatformSettings>();
        settings
            .Setup(p => p.GetStringAsync(
                "billing.frozen_credits.policy_effective_at",
                It.IsAny<string?>(),
                It.IsAny<WarpTalk.Shared.PlatformSettings.SettingContext>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(configured);
        var policy = new Mock<IBillingPolicyRepository>();
        policy
            .Setup(p => p.ReadPolicyValueAsync("frozen_credit_policy_effective_epoch", It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((decimal)recordedEpoch);
        var services = new ServiceCollection()
            .AddSingleton(settings.Object)
            .AddSingleton(policy.Object)
            .BuildServiceProvider();

        var boundary = await SubscriptionExpirationWorker.ResolvePolicyEffectiveAtAsync(services, CancellationToken.None);

        if (expected is null)
        {
            boundary.Should().BeNull();
        }
        else
        {
            boundary.Should().Be(DateTime.Parse(expected, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal));
            boundary!.Value.Kind.Should().Be(DateTimeKind.Utc);
        }
    }
}

