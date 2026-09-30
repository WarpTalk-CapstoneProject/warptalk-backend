using System;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;
using Xunit;

namespace WarpTalk.BillingService.Tests.Application.Services;

/// <summary>
/// WT-878 — the legacy DELETE /subscriptions/workspace/{id} and POST .../reactivate must not fight
/// the #466 Stripe lifecycle: Stripe first, on the PLAN's own subscription id, never an add-on's,
/// and no local change when Stripe refuses.
/// </summary>
public class LegacyCancelReactivateStripeTests
{
    private const string PlanStripeSubscriptionId = "sub_plan_878";

    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<ISubscriptionRepository> _subscriptions = new();
    private readonly Mock<IPlanRepository> _plans = new();
    private readonly Mock<IStripePaymentService> _stripe = new();
    private readonly Mock<IBillingMessagePublisher> _publisher = new();
    private readonly SubscriptionService _sut;
    private readonly Plan _plan = new() { Id = Guid.NewGuid(), Name = "Pro", Price = 500_000m };

    public LegacyCancelReactivateStripeTests()
    {
        _unitOfWork.Setup(u => u.SubscriptionRepository).Returns(_subscriptions.Object);
        _unitOfWork.Setup(u => u.Plans).Returns(_plans.Object);
        _plans.Setup(p => p.GetByIdAsync(_plan.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_plan);

        _sut = new SubscriptionService(
            _unitOfWork.Object,
            new Mock<ILogger<SubscriptionService>>().Object,
            _publisher.Object,
            _stripe.Object,
            Mock.Of<IUsageRateCardAdminService>(),
            Mock.Of<IWorkspaceClient>());
    }

    private Subscription StripeManagedRow(bool autoRenew = true) => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = Guid.NewGuid(),
        PlanId = _plan.Id,
        IsActive = true,
        Status = SubscriptionConstants.SubscriptionStatuses.Active,
        AutoRenew = autoRenew,
        RenewalMode = SubscriptionConstants.RenewalModes.Stripe,
        StripeSubscriptionId = PlanStripeSubscriptionId,
        StripeSubscriptionStatus = SubscriptionConstants.StripeSubscriptionStatuses.Active,
        CurrentPeriodStart = DateTime.UtcNow.AddDays(-10),
        CurrentPeriodEnd = DateTime.UtcNow.AddDays(20),
    };

    private void Returns(Subscription row) =>
        _subscriptions
            .Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(row);

    [Fact]
    public async Task Cancel_StripeManaged_CallsStripeByThePlanSubscriptionId_AndKeepsEntitlements()
    {
        var row = StripeManagedRow();
        Returns(row);
        _stripe
            .Setup(s => s.SetPlanSubscriptionCancelAtPeriodEndAsync(PlanStripeSubscriptionId, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(SubscriptionConstants.StripeSubscriptionStatuses.Active));

        var result = await _sut.CancelSubscriptionAsync(row.WorkspaceId, "too expensive");

        result.IsSuccess.Should().BeTrue();
        _stripe.Verify(s => s.SetPlanSubscriptionCancelAtPeriodEndAsync(PlanStripeSubscriptionId, true, It.IsAny<CancellationToken>()), Times.Once);

        // Never the metadata search: it matched the workspace's add-on subscriptions too.
        _stripe.Verify(s => s.CancelSubscriptionAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _stripe.Verify(
            s => s.SetPlanSubscriptionCancelAtPeriodEndAsync(It.Is<string>(id => id != PlanStripeSubscriptionId), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _stripe.Verify(s => s.CancelStripeSubscriptionAtPeriodEndAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        row.AutoRenew.Should().BeFalse();
        row.Status.Should().Be(SubscriptionConstants.SubscriptionStatuses.Active);
        row.IsActive.Should().BeTrue();
        row.CancellationReason.Should().Be("too expensive");
        row.GrantsPlanEntitlements(DateTime.UtcNow).Should().BeTrue("the paid period runs to its end");
        _subscriptions.Verify(r => r.Update(row), Times.Once);
    }

    [Fact]
    public async Task Cancel_StripeManaged_WhenStripeFails_ReturnsFailureAndChangesNothing()
    {
        var row = StripeManagedRow();
        Returns(row);
        _stripe
            .Setup(s => s.SetPlanSubscriptionCancelAtPeriodEndAsync(PlanStripeSubscriptionId, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<string>("stripe down", ErrorCodes.BillingExternalServiceError));

        var result = await _sut.CancelSubscriptionAsync(row.WorkspaceId, "too expensive");

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.BillingExternalServiceError);
        row.AutoRenew.Should().BeTrue();
        row.Status.Should().Be(SubscriptionConstants.SubscriptionStatuses.Active);
        row.CancellationReason.Should().BeNull();
        _subscriptions.Verify(r => r.Update(It.IsAny<Subscription>()), Times.Never);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        _publisher.Verify(
            p => p.PublishAsync(It.IsAny<string>(), It.IsAny<It.IsAnyType>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Cancel_StripeManaged_WhenStripeThrows_ReturnsFailureAndChangesNothing()
    {
        var row = StripeManagedRow();
        Returns(row);
        _stripe
            .Setup(s => s.SetPlanSubscriptionCancelAtPeriodEndAsync(PlanStripeSubscriptionId, true, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var result = await _sut.CancelSubscriptionAsync(row.WorkspaceId, null);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.BillingExternalServiceError);
        row.AutoRenew.Should().BeTrue();
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Cancel_InvoiceRow_DoesNotCallStripe_AndKeepsEntitlementsUntilPeriodEnd()
    {
        var row = new Subscription
        {
            Id = Guid.NewGuid(),
            WorkspaceId = Guid.NewGuid(),
            PlanId = _plan.Id,
            IsActive = true,
            AutoRenew = true,
            RenewalMode = SubscriptionConstants.RenewalModes.Invoice,
            CurrentPeriodEnd = DateTime.UtcNow.AddDays(5),
        };
        Returns(row);

        var result = await _sut.CancelSubscriptionAsync(row.WorkspaceId, "contract ended");

        result.IsSuccess.Should().BeTrue();
        _stripe.VerifyNoOtherCalls();
        row.AutoRenew.Should().BeFalse();
        row.GrantsPlanEntitlements(DateTime.UtcNow).Should().BeTrue();
        row.GrantsPlanEntitlements(DateTime.UtcNow.AddDays(6)).Should().BeFalse("entitlements end with the period");
    }

    [Fact]
    public async Task Cancel_UnlinkedCardRow_UsesThePlanOnlySearch_AndFailsClosed()
    {
        var row = new Subscription
        {
            Id = Guid.NewGuid(),
            WorkspaceId = Guid.NewGuid(),
            PlanId = _plan.Id,
            IsActive = true,
            AutoRenew = true,
            RenewalMode = SubscriptionConstants.RenewalModes.None,
            CurrentPeriodEnd = DateTime.UtcNow.AddDays(5),
        };
        Returns(row);
        _stripe
            .Setup(s => s.CancelSubscriptionAsync(row.WorkspaceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<bool>("stripe down", ErrorCodes.BillingExternalServiceError));

        var result = await _sut.CancelSubscriptionAsync(row.WorkspaceId, null);

        result.IsSuccess.Should().BeFalse();
        row.AutoRenew.Should().BeTrue();
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Reactivate_StripeManaged_ClearsCancelAtPeriodEndInStripe()
    {
        var row = StripeManagedRow(autoRenew: false);
        Returns(row);
        _stripe
            .Setup(s => s.SetPlanSubscriptionCancelAtPeriodEndAsync(PlanStripeSubscriptionId, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(SubscriptionConstants.StripeSubscriptionStatuses.Active));

        var result = await _sut.ReactivateSubscriptionAsync(row.WorkspaceId);

        result.IsSuccess.Should().BeTrue();
        _stripe.Verify(s => s.SetPlanSubscriptionCancelAtPeriodEndAsync(PlanStripeSubscriptionId, false, It.IsAny<CancellationToken>()), Times.Once);
        row.AutoRenew.Should().BeTrue();
        row.Status.Should().Be(SubscriptionConstants.SubscriptionStatuses.Active);
    }

    [Fact]
    public async Task Reactivate_StripeManaged_WhenStripeFails_ReturnsFailureAndChangesNothing()
    {
        var row = StripeManagedRow(autoRenew: false);
        Returns(row);
        _stripe
            .Setup(s => s.SetPlanSubscriptionCancelAtPeriodEndAsync(PlanStripeSubscriptionId, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<string>("stripe down", ErrorCodes.BillingExternalServiceError));

        var result = await _sut.ReactivateSubscriptionAsync(row.WorkspaceId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.BillingExternalServiceError);
        row.AutoRenew.Should().BeFalse();
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Reactivate_StripeSubscriptionAlreadyEnded_RequiresCheckout()
    {
        var row = StripeManagedRow(autoRenew: false);
        row.StripeSubscriptionStatus = SubscriptionConstants.StripeSubscriptionStatuses.Canceled;
        Returns(row);

        var result = await _sut.ReactivateSubscriptionAsync(row.WorkspaceId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(StripeSubscriptionLifecycleService.AutoRenewRequiresCheckoutCode);
        _stripe.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Reactivate_OneOffCardRow_RequiresCheckout()
    {
        var row = new Subscription
        {
            Id = Guid.NewGuid(),
            WorkspaceId = Guid.NewGuid(),
            PlanId = _plan.Id,
            IsActive = true,
            AutoRenew = false,
            RenewalMode = SubscriptionConstants.RenewalModes.None,
            CurrentPeriodEnd = DateTime.UtcNow.AddDays(5),
        };
        Returns(row);

        var result = await _sut.ReactivateSubscriptionAsync(row.WorkspaceId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(StripeSubscriptionLifecycleService.AutoRenewRequiresCheckoutCode);
        row.AutoRenew.Should().BeFalse();
        _stripe.VerifyNoOtherCalls();
    }
}
