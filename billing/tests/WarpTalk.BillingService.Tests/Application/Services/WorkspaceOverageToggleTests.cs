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
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;
using Xunit;

namespace WarpTalk.BillingService.Tests.Application.Services;

/// <summary>
/// Whether a meeting keeps translating after the credits run out.
///
/// The engine for this already existed — `settle_usage_charge` lets `credits_remaining` go
/// negative, tracks `overage_credits_this_cycle`, and suspends at `overage_cap_credits`. Nothing
/// exposed the switch, so the answer was always "no" and a meeting simply stopped mid-sentence.
///
/// THE CAP IS NOT THE OWNER'S TO SET. `UpdateContractTermsAsync` can write any cap and is
/// system-admin-only for exactly that reason: letting a customer choose their own ceiling is
/// letting them issue themselves credit. These tests pin that this endpoint only moves between
/// off and the allowance the PLAN already grants.
/// </summary>
public class WorkspaceOverageToggleTests
{
    private static readonly Guid WorkspaceId = Guid.NewGuid();
    private static readonly Guid PlanId = Guid.NewGuid();

    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<ISubscriptionRepository> _subs = new();
    private readonly Mock<IPlanRepository> _plans = new();
    private readonly SubscriptionService _sut;

    public WorkspaceOverageToggleTests()
    {
        _unitOfWork.Setup(u => u.SubscriptionRepository).Returns(_subs.Object);
        _unitOfWork.Setup(u => u.Plans).Returns(_plans.Object);
        _unitOfWork.Setup(u => u.CreditTransactionRepository).Returns(Mock.Of<ICreditTransactionRepository>());
        _unitOfWork.Setup(u => u.PaymentRepository).Returns(Mock.Of<IPaymentRepository>());
        _unitOfWork.Setup(u => u.InvoiceRepository).Returns(Mock.Of<IInvoiceRepository>());

        _sut = new SubscriptionService(
            _unitOfWork.Object,
            Mock.Of<ILogger<SubscriptionService>>(),
            Mock.Of<IBillingMessagePublisher>(),
            Mock.Of<IStripePaymentService>(),
            Mock.Of<IUsageRateCardAdminService>(),
            Mock.Of<IWorkspaceClient>(),
            Mock.Of<IAiServiceStateStore>());
    }

    private Subscription Given(
        int planCap,
        int? overrideCap = null,
        int overageUsed = 0,
        string state = SubscriptionConstants.ServiceStates.Healthy,
        string? suspendedReason = null)
    {
        var sub = new Subscription
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            PlanId = PlanId,
            IsActive = true,
            CreditsRemaining = 0,
            OverageCreditsThisCycle = overageUsed,
            OverageCapCreditsOverride = overrideCap,
            ServiceState = state,
            SuspendedReason = suspendedReason,
        };

        _subs.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(sub);
        _plans.Setup(r => r.GetByIdAsync(PlanId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Plan { Id = PlanId, OverageCapCredits = planCap });
        return sub;
    }

    // ── Reading ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_ReportsOff_WhenTheOverridePinsItToZero()
    {
        Given(planCap: 50_000, overrideCap: 0);

        var result = await _sut.GetOverageSettingAsync(WorkspaceId);

        result.Value!.Enabled.Should().BeFalse();
        result.Value.EffectiveCapCredits.Should().Be(0);
        // The plan's own allowance is still reported, so the page can offer to turn it back on.
        result.Value.PlanCapCredits.Should().Be(50_000);
    }

    [Fact]
    public async Task Get_FallsBackToThePlansCap_WhenThereIsNoOverride()
    {
        Given(planCap: 50_000, overrideCap: null);

        var result = await _sut.GetOverageSettingAsync(WorkspaceId);

        result.Value!.Enabled.Should().BeTrue();
        result.Value.EffectiveCapCredits.Should().Be(50_000);
    }

    [Fact]
    public async Task Get_DistinguishesNotAvailableFromSwitchedOff()
    {
        // A plan with no allowance at all. The page must not offer a switch that cannot do
        // anything — PlanCapCredits of 0 is what tells it so.
        Given(planCap: 0, overrideCap: null);

        var result = await _sut.GetOverageSettingAsync(WorkspaceId);

        result.Value!.Enabled.Should().BeFalse();
        result.Value.PlanCapCredits.Should().Be(0);
    }

    // ── Writing ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Enable_ClearsTheOverrideRatherThanCopyingTodaysCap()
    {
        // The override exists to DIFFER from the plan. Writing the plan's current number into it
        // would freeze it, and a later plan change would silently not reach this workspace.
        var sub = Given(planCap: 50_000, overrideCap: 0);

        var result = await _sut.SetOverageAsync(WorkspaceId, new SetWorkspaceOverageRequest(true));

        result.IsSuccess.Should().BeTrue(result.Error);
        sub.OverageCapCreditsOverride.Should().BeNull();
        result.Value!.EffectiveCapCredits.Should().Be(50_000);
    }

    [Fact]
    public async Task Disable_PinsItToZero()
    {
        var sub = Given(planCap: 50_000, overrideCap: null);

        await _sut.SetOverageAsync(WorkspaceId, new SetWorkspaceOverageRequest(false));

        sub.OverageCapCreditsOverride.Should().Be(0);
    }

    [Fact]
    public async Task Enable_IsRefusedOnAPlanThatOffersNoOverage()
    {
        // Reporting success here would be the worst outcome: the owner believes their meetings
        // will keep running, and the next one still stops dead at zero.
        var sub = Given(planCap: 0, overrideCap: 0);

        var result = await _sut.SetOverageAsync(WorkspaceId, new SetWorkspaceOverageRequest(true));

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
        sub.OverageCapCreditsOverride.Should().Be(0);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Enable_ResumesAWorkspaceSuspendedForOverage_WhenThereIsRoomUnderTheCap()
    {
        var sub = Given(
            planCap: 50_000,
            overrideCap: 0,
            overageUsed: 10_000,
            state: SubscriptionConstants.ServiceStates.Suspended,
            suspendedReason: SubscriptionConstants.SuspendedReasons.OverageCap);

        await _sut.SetOverageAsync(WorkspaceId, new SetWorkspaceOverageRequest(true));

        sub.ServiceState.Should().Be(SubscriptionConstants.ServiceStates.Healthy);
    }

    [Fact]
    public async Task Enable_DoesNotResume_WhenTheCycleHasAlreadySpentTheWholeCap()
    {
        // Resuming here would hand back a service the workspace has already exhausted, and the
        // next settlement would suspend it again immediately.
        var sub = Given(
            planCap: 50_000,
            overrideCap: 0,
            overageUsed: 50_000,
            state: SubscriptionConstants.ServiceStates.Suspended,
            suspendedReason: SubscriptionConstants.SuspendedReasons.OverageCap);

        await _sut.SetOverageAsync(WorkspaceId, new SetWorkspaceOverageRequest(true));

        sub.ServiceState.Should().Be(SubscriptionConstants.ServiceStates.Suspended);
    }

    [Fact]
    public async Task Disable_NeverSuspends()
    {
        // Switching the allowance off must not be a one-way door that also cuts the service.
        // The next settlement decides that, from the balance.
        var sub = Given(planCap: 50_000, overrideCap: null, overageUsed: 10_000);

        await _sut.SetOverageAsync(WorkspaceId, new SetWorkspaceOverageRequest(false));

        sub.ServiceState.Should().Be(SubscriptionConstants.ServiceStates.Healthy);
    }

    [Fact]
    public async Task BothPaths_FailCleanly_WhenTheWorkspaceHasNoSubscription()
    {
        _subs.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Subscription?)null);

        (await _sut.GetOverageSettingAsync(WorkspaceId)).IsSuccess.Should().BeFalse();
        (await _sut.SetOverageAsync(WorkspaceId, new SetWorkspaceOverageRequest(true))).IsSuccess.Should().BeFalse();
    }
}
