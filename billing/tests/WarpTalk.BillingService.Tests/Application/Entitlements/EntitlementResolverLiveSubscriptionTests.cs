using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using WarpTalk.BillingService.Application.Entitlements;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Domain.Services;
using Xunit;

namespace WarpTalk.BillingService.Tests.Application.Entitlements;

/// <summary>
/// WT-878 (bug A): a workspace that re-subscribed after expiry, or changed plan, has several
/// subscription rows. Entitlements must be computed from the LIVE one whatever order the rows were
/// written in. An unordered pick of a dead row dropped the workspace to the platform floor
/// (5 rooms, 2 participants, voice clone off) while billing showed an active plan.
///
/// The repository is faked with the same <see cref="SubscriptionSelection"/> expressions the real
/// one runs in SQL, so the selection rule is exercised end to end through the resolver.
/// </summary>
public class EntitlementResolverLiveSubscriptionTests
{
    private static readonly DateTime Now = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
    private readonly Guid _workspaceId = Guid.NewGuid();

    private static Plan PlanWith(string slug, int maxActiveRooms, int maxParticipants, bool voiceClone) => new()
    {
        Id = Guid.NewGuid(),
        Slug = slug,
        Name = slug,
        Tier = SubscriptionConstants.Tiers.Startup,
        MaxLanguages = 5,
        MaxActiveRooms = maxActiveRooms,
        MaxParticipants = maxParticipants,
        VoiceCloneEnabled = voiceClone,
        AiAssistantEnabled = true,
        GlossaryEnabled = true
    };

    private Subscription OldExpiredRow(Plan plan) => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = _workspaceId,
        PlanId = plan.Id,
        IsActive = false,
        Status = SubscriptionConstants.SubscriptionStatuses.Expired,
        CurrentPeriodStart = Now.AddMonths(-3),
        CurrentPeriodEnd = Now.AddMonths(-2),
        CreatedAt = Now.AddMonths(-3)
    };

    private Subscription NewLiveRow(Plan plan) => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = _workspaceId,
        PlanId = plan.Id,
        IsActive = true,
        Status = SubscriptionConstants.SubscriptionStatuses.Active,
        CurrentPeriodStart = Now.AddDays(-5),
        CurrentPeriodEnd = Now.AddDays(25),
        CreatedAt = Now.AddDays(-5)
    };

    private static EntitlementResolver Resolver(IReadOnlyList<Subscription> rows, params Plan[] plans)
    {
        var subscriptions = new Mock<ISubscriptionRepository>();
        subscriptions
            .Setup(r => r.GetCurrentForWorkspaceAsync(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid workspaceId, bool _, CancellationToken _) => SubscriptionSelection
                .OrderCurrentFirst(SubscriptionSelection.ForWorkspace(rows.AsQueryable(), workspaceId))
                .FirstOrDefault());

        var planRepository = new Mock<IPlanRepository>();
        planRepository
            .Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => plans.FirstOrDefault(p => p.Id == id));

        var overrides = new Mock<IWorkspaceEntitlementOverrideRepository>();
        overrides
            .Setup(r => r.GetForWorkspaceAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<WorkspaceEntitlementOverride>());

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.SubscriptionRepository).Returns(subscriptions.Object);
        unitOfWork.SetupGet(u => u.Plans).Returns(planRepository.Object);
        unitOfWork.SetupGet(u => u.WorkspaceEntitlementOverrides).Returns(overrides.Object);

        return new EntitlementResolver(unitOfWork.Object, new FixedTimeProvider(Now));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_live_row_wins_over_an_old_inactive_row_in_either_insertion_order(bool oldRowFirst)
    {
        var oldPlan = PlanWith("starter", maxActiveRooms: 10, maxParticipants: 10, voiceClone: false);
        var newPlan = PlanWith("business", maxActiveRooms: 50, maxParticipants: 200, voiceClone: true);
        var old = OldExpiredRow(oldPlan);
        var live = NewLiveRow(newPlan);
        var rows = oldRowFirst ? new[] { old, live } : new[] { live, old };

        var map = await Resolver(rows, oldPlan, newPlan).ResolveAsync(_workspaceId);

        map.HasActiveSubscription.Should().BeTrue("the workspace's live subscription is the one in force");
        map.PlanSlug.Should().Be("business");
        map.Number(EntitlementConstants.Keys.MaxActiveRooms).Should().Be(50);
        map.Number(EntitlementConstants.Keys.MaxParticipants).Should().Be(200);
        map[EntitlementConstants.Keys.VoiceClone].AsFlag().Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Rebuying_the_same_plan_resolves_the_active_row_not_the_platform_floor(bool oldRowFirst)
    {
        // Same plan re-bought after expiry: both rows point at the same plan, so only liveness decides.
        var plan = PlanWith("business", maxActiveRooms: 50, maxParticipants: 200, voiceClone: true);
        var rows = oldRowFirst
            ? new[] { OldExpiredRow(plan), NewLiveRow(plan) }
            : new[] { NewLiveRow(plan), OldExpiredRow(plan) };

        var map = await Resolver(rows, plan).ResolveAsync(_workspaceId);

        map.HasActiveSubscription.Should().BeTrue();
        map[EntitlementConstants.Keys.MaxActiveRooms].Source.Should().NotBe(EntitlementConstants.Sources.PlatformDefault);
        map.Number(EntitlementConstants.Keys.MaxActiveRooms).Should().Be(50);
    }

    [Fact]
    public async Task With_no_live_row_the_newest_inactive_row_is_still_read()
    {
        // Its contract overrides apply even without an active subscription (layer 3), so an expired
        // workspace keeps them.
        var plan = PlanWith("business", maxActiveRooms: 50, maxParticipants: 200, voiceClone: true);
        var older = OldExpiredRow(plan);
        older.EntitlementOverrides = "{\"max_active_rooms\": 3}";
        var newer = OldExpiredRow(plan);
        newer.CurrentPeriodStart = Now.AddMonths(-1);
        newer.CreatedAt = Now.AddMonths(-1);
        newer.EntitlementOverrides = "{\"max_active_rooms\": 7}";

        var map = await Resolver(new[] { older, newer }, plan).ResolveAsync(_workspaceId);

        map.HasActiveSubscription.Should().BeFalse();
        map[EntitlementConstants.Keys.MaxActiveRooms].Source.Should().Be(EntitlementConstants.Sources.ContractOverride);
        map.Number(EntitlementConstants.Keys.MaxActiveRooms).Should().Be(7, "the newest row's contract is read");
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }
}
