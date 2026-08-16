using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using WarpTalk.BillingService.Application.Entitlements;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Options;
using WarpTalk.BillingService.Infrastructure.Workers;
using Xunit;

namespace WarpTalk.BillingService.Tests.Infrastructure.Workers;

/// <summary>
/// WT-430. Consumers enforce from a local snapshot that only three billing methods ever refresh,
/// all of them reacting to a mutation made through billing. A change arriving any other way left
/// every consumer enforcing a stale answer permanently — production spent two days on
/// platform-default quotas after a subscription status was corrected directly, with a healthy
/// publish path that simply had no reason to fire.
/// </summary>
public class EntitlementReconcileWorkerTests
{
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<ISubscriptionRepository> _subscriptions = new();
    private readonly Mock<IEntitlementChangePublisher> _publisher = new();
    private readonly List<(Guid WorkspaceId, string Reason)> _enqueued = new();

    private EntitlementReconcileWorker Build(int intervalMinutes = 60)
    {
        _unitOfWork.Setup(u => u.SubscriptionRepository).Returns(_subscriptions.Object);
        _unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        _publisher
            .Setup(p => p.EnqueueAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, string, CancellationToken>((id, reason, _) => _enqueued.Add((id, reason)))
            .Returns(Task.CompletedTask);

        var services = new ServiceCollection();
        services.AddSingleton(_unitOfWork.Object);
        services.AddSingleton(_publisher.Object);

        return new EntitlementReconcileWorker(
            services.BuildServiceProvider(),
            Mock.Of<ILogger<EntitlementReconcileWorker>>(),
            Options.Create(new BillingWorkerOptions
            {
                EntitlementReconcileIntervalMinutes = intervalMinutes
            }));
    }

    private static Subscription For(Guid workspaceId) => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = workspaceId,
        Status = SubscriptionConstants.SubscriptionStatuses.Active,
        IsActive = true,
    };

    [Fact]
    public async Task EverySubscribedWorkspaceIsRepublished()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        _subscriptions
            .Setup(r => r.GetActiveSubscriptionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Subscription> { For(a), For(b) });

        await Build().ReconcileAsync(CancellationToken.None);

        _enqueued.Select(e => e.WorkspaceId).Should().BeEquivalentTo(new[] { a, b });
        _enqueued.Should().OnlyContain(e => e.Reason == EntitlementConstants.Reasons.Backfill);
    }

    [Fact]
    public async Task AWorkspaceWithTwoSubscriptionRowsIsEnqueuedOnce()
    {
        // The resolver answers per workspace, not per row, so a second row would only cost a
        // duplicate event.
        var workspaceId = Guid.NewGuid();
        _subscriptions
            .Setup(r => r.GetActiveSubscriptionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Subscription> { For(workspaceId), For(workspaceId) });

        await Build().ReconcileAsync(CancellationToken.None);

        _enqueued.Should().ContainSingle().Which.WorkspaceId.Should().Be(workspaceId);
    }

    [Fact]
    public async Task NothingIsCommittedWhenThereIsNothingToPublish()
    {
        _subscriptions
            .Setup(r => r.GetActiveSubscriptionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Subscription>());

        await Build().ReconcileAsync(CancellationToken.None);

        _enqueued.Should().BeEmpty();
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TheSweepCommitsExactlyOnce()
    {
        // EnqueueAsync writes through the unit of work and deliberately does not commit, so without
        // this single SaveChangesAsync nothing would ever reach the outbox — the sweep would run,
        // log, and publish nothing.
        _subscriptions
            .Setup(r => r.GetActiveSubscriptionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Subscription> { For(Guid.NewGuid()), For(Guid.NewGuid()) });

        await Build().ReconcileAsync(CancellationToken.None);

        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AnEmptyWorkspaceIdIsSkipped()
    {
        _subscriptions
            .Setup(r => r.GetActiveSubscriptionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Subscription> { For(Guid.Empty) });

        await Build().ReconcileAsync(CancellationToken.None);

        _enqueued.Should().BeEmpty();
    }

    [Fact]
    public async Task AZeroIntervalStopsTheWorkerWithoutSweeping()
    {
        // The off switch has to actually switch it off, not merely sweep on a zero delay.
        _subscriptions
            .Setup(r => r.GetActiveSubscriptionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Subscription> { For(Guid.NewGuid()) });

        var worker = Build(intervalMinutes: 0);
        await worker.StartAsync(CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);

        _enqueued.Should().BeEmpty();
    }
}
