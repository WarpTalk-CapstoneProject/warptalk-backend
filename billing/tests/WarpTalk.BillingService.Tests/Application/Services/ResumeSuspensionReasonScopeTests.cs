using System;
using System.Linq.Expressions;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using WarpTalk.BillingService.API.Controllers;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Authorization;
using Xunit;

namespace WarpTalk.BillingService.Tests.Application.Services;

/// <summary>
/// WT-878 — POST /subscriptions/workspace/{id}/resume is reason-scoped. A workspace Owner/Admin
/// may lift only overage_cap (with room under the cap); trial_ended and invoice_overdue are
/// refused. Platform staff keep the unrestricted resume.
/// </summary>
public class ResumeSuspensionReasonScopeTests
{
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<ISubscriptionRepository> _subscriptions = new();
    private readonly Mock<IPlanRepository> _plans = new();
    private readonly SubscriptionService _sut;
    private readonly Plan _plan = new() { Id = Guid.NewGuid(), Name = "Pro", Price = 500_000m, OverageCapCredits = 1_000 };

    public ResumeSuspensionReasonScopeTests()
    {
        _unitOfWork.Setup(u => u.SubscriptionRepository).Returns(_subscriptions.Object);
        _unitOfWork.Setup(u => u.Plans).Returns(_plans.Object);
        _plans.Setup(p => p.GetByIdAsync(_plan.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_plan);

        _sut = new SubscriptionService(
            _unitOfWork.Object,
            new Mock<ILogger<SubscriptionService>>().Object,
            Mock.Of<IBillingMessagePublisher>(),
            Mock.Of<IStripePaymentService>(),
            Mock.Of<IUsageRateCardAdminService>(),
            Mock.Of<IWorkspaceClient>());
    }

    private Subscription Suspended(string reason, int creditsRemaining = 0, int overageUsed = 0) => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = Guid.NewGuid(),
        PlanId = _plan.Id,
        IsActive = true,
        ServiceState = SubscriptionConstants.ServiceStates.Suspended,
        SuspendedReason = reason,
        CreditsRemaining = creditsRemaining,
        OverageCreditsThisCycle = overageUsed,
        CurrentPeriodEnd = DateTime.UtcNow.AddDays(10),
    };

    private void Returns(Subscription row)
    {
        _subscriptions
            .Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(row);
        _subscriptions.Setup(r => r.GetActiveByWorkspaceIdAsync(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(row);
    }

    [Theory]
    [InlineData(SubscriptionConstants.SuspendedReasons.TrialEnded)]
    [InlineData(SubscriptionConstants.SuspendedReasons.InvoiceOverdue)]
    [InlineData("admin_hold")]
    public async Task Owner_CannotLift_NonOverageReasons(string reason)
    {
        var row = Suspended(reason, creditsRemaining: 500);
        Returns(row);

        var result = await _sut.ResumeSubscriptionAsync(row.WorkspaceId, new ResumeSubscriptionRequest("please"));

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(SubscriptionService.ResumeNotAllowedCode);
        row.ServiceState.Should().Be(SubscriptionConstants.ServiceStates.Suspended);
        row.SuspendedReason.Should().Be(reason);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Owner_CanLift_OverageCap_WhenThereIsRoom()
    {
        var row = Suspended(SubscriptionConstants.SuspendedReasons.OverageCap, creditsRemaining: 0, overageUsed: 200);
        Returns(row);

        var result = await _sut.ResumeSubscriptionAsync(row.WorkspaceId, new ResumeSubscriptionRequest());

        result.IsSuccess.Should().BeTrue();
        row.ServiceState.Should().Be(SubscriptionConstants.ServiceStates.Healthy);
        row.SuspendedReason.Should().BeNull();
    }

    [Fact]
    public async Task Owner_CannotLift_OverageCap_WhileStillOverTheCap()
    {
        var row = Suspended(SubscriptionConstants.SuspendedReasons.OverageCap, creditsRemaining: 0, overageUsed: 1_000);
        Returns(row);

        var result = await _sut.ResumeSubscriptionAsync(row.WorkspaceId, new ResumeSubscriptionRequest());

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(SubscriptionService.ResumeNotAllowedCode);
        row.ServiceState.Should().Be(SubscriptionConstants.ServiceStates.Suspended);
    }

    [Theory]
    [InlineData(SubscriptionConstants.SuspendedReasons.TrialEnded)]
    [InlineData(SubscriptionConstants.SuspendedReasons.InvoiceOverdue)]
    [InlineData(SubscriptionConstants.SuspendedReasons.OverageCap)]
    public async Task PlatformStaff_CanStillLift_AnyReason(string reason)
    {
        var row = Suspended(reason, overageUsed: 5_000);
        Returns(row);

        var result = await _sut.ResumeSubscriptionAsync(row.WorkspaceId, new ResumeSubscriptionRequest("support"), liftAnyReason: true);

        result.IsSuccess.Should().BeTrue();
        row.ServiceState.Should().Be(SubscriptionConstants.ServiceStates.Healthy);
        row.SuspendedReason.Should().BeNull();
    }

    // ---- controller: who counts as platform staff ---------------------------------------------

    private static SubscriptionsController Controller(
        Mock<ISubscriptionService> service,
        Mock<IStaffAccessResolver> staff,
        ClaimsPrincipal user) =>
        new(service.Object, Mock.Of<IStripeSubscriptionLifecycleService>(), staff.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } },
        };

    [Fact]
    public async Task Controller_StaffWithManagePermission_ResumesWithoutReasonScope()
    {
        var workspaceId = Guid.NewGuid();
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Role, StaffClaims.StaffRoleHint) }, "test"));
        var staff = new Mock<IStaffAccessResolver>();
        staff
            .Setup(s => s.HasPermissionAsync(user, AdminPermissions.BillingSubscriptionsManage, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var service = new Mock<ISubscriptionService>();
        service
            .Setup(s => s.ResumeSubscriptionAsync(workspaceId, It.IsAny<ResumeSubscriptionRequest>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new SubscriptionDto(
                Guid.NewGuid(), Guid.NewGuid(), workspaceId, Guid.NewGuid(), "Pro", 0m, "active", 0, 0,
                DateTime.UtcNow, DateTime.UtcNow.AddDays(1), true, false, DateTime.UtcNow, null)));

        var response = await Controller(service, staff, user)
            .ResumeSubscription(workspaceId, new ResumeSubscriptionRequest(), CancellationToken.None);

        response.Result.Should().BeOfType<OkObjectResult>();
        service.Verify(s => s.ResumeSubscriptionAsync(workspaceId, It.IsAny<ResumeSubscriptionRequest>(), true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Controller_WorkspaceOwner_IsReasonScoped_AndRefusalIs403()
    {
        var workspaceId = Guid.NewGuid();
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) }, "test"));
        var staff = new Mock<IStaffAccessResolver>();
        var service = new Mock<ISubscriptionService>();
        service
            .Setup(s => s.ResumeSubscriptionAsync(workspaceId, It.IsAny<ResumeSubscriptionRequest>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<SubscriptionDto>(SubscriptionService.ResumeTrialEndedMessage, SubscriptionService.ResumeNotAllowedCode));

        var response = await Controller(service, staff, user)
            .ResumeSubscription(workspaceId, new ResumeSubscriptionRequest(), CancellationToken.None);

        response.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        staff.Verify(s => s.HasPermissionAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
