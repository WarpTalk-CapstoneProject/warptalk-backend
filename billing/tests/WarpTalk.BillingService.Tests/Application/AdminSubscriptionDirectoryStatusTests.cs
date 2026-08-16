using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.BillingService.Tests.Application;

/// <summary>
/// WT-439. The admin subscriptions page sends <c>status=all</c> on its DEFAULT load — so
/// rejecting "all" made the page 400 on first open, 100% of the time, before the admin touched
/// a single control. The sibling AdminUserService already treats "all" as a member of its
/// status vocabulary; billing's directory was the one that forgot.
///
/// The failure also misled the reporter: the request carried both status=all and
/// sort=period_end_asc, and the 400's visibility on the sort-bearing URL pointed the
/// investigation at sorting when the sort was always valid.
/// </summary>
public class AdminSubscriptionDirectoryStatusTests
{
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<ISubscriptionRepository> _subscriptions = new();
    private readonly AdminSubscriptionService _service;

    public AdminSubscriptionDirectoryStatusTests()
    {
        _unitOfWork.Setup(u => u.SubscriptionRepository).Returns(_subscriptions.Object);
        _subscriptions
            .Setup(r => r.GetAdminDirectoryAsync(
                It.IsAny<AdminSubscriptionFilter>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(((IReadOnlyList<AdminSubscriptionRow>)new List<AdminSubscriptionRow>(), 0));

        _service = new AdminSubscriptionService(
            _unitOfWork.Object,
            Mock.Of<ILogger<AdminSubscriptionService>>());
    }

    private static AdminSubscriptionDirectoryQuery Query(string? status) =>
        new() { Status = status, Sort = "period_end_asc", Page = 1, PageSize = 20 };

    [Fact]
    public async Task TheDefaultPageLoadIsAccepted()
    {
        // The exact request from the bug report: status=all, sort=period_end_asc, page 1.
        var result = await _service.GetDirectoryAsync(Query("all"));

        result.IsSuccess.Should().BeTrue(result.Error);
    }

    [Fact]
    public async Task AllMeansNoFilter()
    {
        AdminSubscriptionFilter? seen = null;
        _subscriptions
            .Setup(r => r.GetAdminDirectoryAsync(
                It.IsAny<AdminSubscriptionFilter>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<AdminSubscriptionFilter, int, int, CancellationToken>((f, _, _, _) => seen = f)
            .ReturnsAsync(((IReadOnlyList<AdminSubscriptionRow>)new List<AdminSubscriptionRow>(), 0));

        await _service.GetDirectoryAsync(Query("ALL"));

        seen.Should().NotBeNull();
        seen!.Status.Should().BeNull("'all' is the web's spelling of 'no filter', not a status to match rows against");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("active")]
    [InlineData("cancelled")]
    public async Task RealStatusesAndAbsenceStillWork(string? status)
    {
        var result = await _service.GetDirectoryAsync(Query(status));

        result.IsSuccess.Should().BeTrue(result.Error);
    }

    [Fact]
    public async Task AGenuinelyUnknownStatusIsStillRejected()
    {
        var result = await _service.GetDirectoryAsync(Query("frobnicated"));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("Unknown status");
    }
}
