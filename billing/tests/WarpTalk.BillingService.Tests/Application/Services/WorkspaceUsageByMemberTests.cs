using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using Moq;
using WarpTalk.BillingService.API.Authorization;
using WarpTalk.BillingService.API.Controllers;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;
using Xunit;

namespace WarpTalk.BillingService.Tests.Application.Services;

/// <summary>
/// WT-413 — a workspace owner could not see which member was spending the credits.
///
/// The data was already there and needed no migration: usage_records carries UserId,
/// WorkspaceId and CreditsConsumed, and production has 474 of 474 rows attributed
/// (3 users, 546 credits, split across TRANSLATION and AUDIO_DUBBING_STANDARD). What was
/// missing was an aggregate, an endpoint and a gate.
/// </summary>
public class WorkspaceUsageByMemberTests
{
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IUsageRecordRepository> _usage = new();
    private readonly CreditService _service;

    private readonly Guid _workspaceId = Guid.NewGuid();
    private readonly Guid _alice = Guid.NewGuid();
    private readonly Guid _bob = Guid.NewGuid();

    public WorkspaceUsageByMemberTests()
    {
        _unitOfWork.Setup(u => u.UsageRecordRepository).Returns(_usage.Object);
        _service = new CreditService(
            _unitOfWork.Object,
            Mock.Of<ILogger<CreditService>>(),
            Mock.Of<IUsageSettlementService>(),
            Mock.Of<IWorkspaceClient>());
    }

    private void UsageIs(params WorkspaceMemberUsage[] rows) =>
        _usage.Setup(r => r.GetUsageByMemberAsync(
                _workspaceId, It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(rows.ToList());

    [Fact]
    public async Task EachMembersSpendIsReportedSeparately()
    {
        UsageIs(
            new WorkspaceMemberUsage(_alice, 250, 224, DateTime.UtcNow),
            new WorkspaceMemberUsage(_bob, 222, 182, DateTime.UtcNow));

        var result = await _service.GetUsageByMemberAsync(_workspaceId, null, null);

        Assert.True(result.IsSuccess);
        var members = result.Value!.Members;
        Assert.Equal(2, members.Count);
        Assert.Equal(250, members.Single(m => m.UserId == _alice).CreditsConsumed);
        Assert.Equal(222, members.Single(m => m.UserId == _bob).CreditsConsumed);
    }

    /// <summary>
    /// The number above the table must equal the sum of the table. Reading the total off the
    /// subscription balance instead would answer a different question — what is LEFT, not what
    /// these people SPENT in this window — and an owner who adds up the rows would find the two
    /// disagreeing and trust neither.
    /// </summary>
    [Fact]
    public async Task TheTotalIsTheSumOfTheRowsShown()
    {
        UsageIs(
            new WorkspaceMemberUsage(_alice, 250, 224, DateTime.UtcNow),
            new WorkspaceMemberUsage(_bob, 222, 182, DateTime.UtcNow));

        var result = await _service.GetUsageByMemberAsync(_workspaceId, null, null);

        Assert.Equal(472, result.Value!.TotalCreditsConsumed);
        Assert.Equal(result.Value.Members.Sum(m => m.CreditsConsumed), result.Value.TotalCreditsConsumed);
    }

    [Fact]
    public async Task AWorkspaceWithNoUsageIsAnEmptyTableNotAnError()
    {
        UsageIs();

        var result = await _service.GetUsageByMemberAsync(_workspaceId, null, null);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value!.Members);
        Assert.Equal(0, result.Value.TotalCreditsConsumed);
    }

    /// <summary>
    /// A backwards window is a caller mistake. Returning an empty list would read as "nobody
    /// spent anything", which is the one answer a spend dashboard must never give wrongly.
    /// </summary>
    [Fact]
    public async Task ABackwardsDateRangeIsRefusedRatherThanReportedAsZero()
    {
        var result = await _service.GetUsageByMemberAsync(
            _workspaceId, DateTime.UtcNow, DateTime.UtcNow.AddDays(-7));

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
        _usage.Verify(r => r.GetUsageByMemberAsync(
            It.IsAny<Guid>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task TheRequestedWindowIsPassedThroughToTheQuery()
    {
        var from = DateTime.UtcNow.AddDays(-30);
        var to = DateTime.UtcNow;
        UsageIs();

        await _service.GetUsageByMemberAsync(_workspaceId, from, to);

        _usage.Verify(r => r.GetUsageByMemberAsync(_workspaceId, from, to, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// This endpoint exposes the whole workspace's spend, so it carries the same gate as the
    /// balance and history endpoints beside it. An ordinary member must not read it.
    /// </summary>
    [Fact]
    public void TheEndpointIsOwnerOrAdminOnly()
    {
        var action = typeof(CreditsController).GetMethod(nameof(CreditsController.GetUsageByMember));
        Assert.NotNull(action);

        var roleFilter = action!.GetCustomAttribute<RequireWorkspaceRoleAttribute>();
        Assert.NotNull(roleFilter);

        var roles = Assert.IsType<string[]>(Assert.Single(roleFilter!.Arguments!));
        Assert.Contains("Owner", roles);
        Assert.Contains("Admin", roles);
        Assert.DoesNotContain("Member", roles);
        Assert.Null(action.GetCustomAttribute<AllowAnonymousAttribute>());
    }
}
