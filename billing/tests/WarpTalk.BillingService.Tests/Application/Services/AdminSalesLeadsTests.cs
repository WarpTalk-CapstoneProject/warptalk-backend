using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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
/// The platform-wide sales lead inbox: who may open it, what order it reads in, and what a status
/// change is allowed to write.
/// </summary>
public class AdminSalesLeadsTests
{
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<ISalesInquiryRepository> _repository = new();
    private readonly SalesInquiryService _service;

    public AdminSalesLeadsTests()
    {
        _unitOfWork.Setup(u => u.SalesInquiryRepository).Returns(_repository.Object);
        _service = new SalesInquiryService(_unitOfWork.Object, new Mock<ISubscriptionService>().Object);
    }

    private static SalesInquiry Lead(string status, DateTime createdAt, Guid? workspaceId = null) => new()
    {
        Id = Guid.NewGuid(),
        FirstName = "Ada",
        LastName = "Lovelace",
        WorkEmail = "ada@example.com",
        Company = "Analytical",
        RequestType = "enterprise",
        CurrentMonthlyMeetingVolume = "50",
        Status = status,
        WorkspaceId = workspaceId,
        CreatedAt = createdAt,
        UpdatedAt = createdAt,
    };

    // ── Authorization ────────────────────────────────────────

    [Fact]
    public void Controller_is_gated_on_the_shared_system_admin_policy()
    {
        var authorize = typeof(AdminSalesLeadsController)
            .GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .SingleOrDefault();

        authorize.Should().NotBeNull();
        authorize!.Policy.Should().Be(SystemAdminAuthorization.PolicyName);
        authorize.Roles.Should().BeNull();

        typeof(AdminSalesLeadsController)
            .GetMethods()
            .Where(m => m.GetCustomAttribute<AllowAnonymousAttribute>() is not null)
            .Should().BeEmpty();
    }

    [Fact]
    public void Controller_lives_under_the_gateway_admin_billing_prefix()
    {
        var route = typeof(AdminSalesLeadsController).GetCustomAttribute<RouteAttribute>();

        route.Should().NotBeNull();
        route!.Template.Should().StartWith("api/v1/admin/billing/");
    }

    // ── Listing ──────────────────────────────────────────────

    [Fact]
    public async Task Admin_list_reads_newest_first_regardless_of_status()
    {
        var now = DateTime.UtcNow;
        var oldNew = Lead(SalesInquiryConstants.Statuses.New, now.AddDays(-30));
        var freshClosed = Lead(SalesInquiryConstants.Statuses.Closed, now.AddMinutes(-5));
        var middleQuoted = Lead(SalesInquiryConstants.Statuses.Quoted, now.AddDays(-2));
        var rows = new List<SalesInquiry> { oldNew, freshClosed, middleQuoted };

        _repository
            .Setup(r => r.CountAsync(It.IsAny<Expression<Func<SalesInquiry, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(rows.Count);
        _repository
            .Setup(r => r.GetPagedAsync(
                It.IsAny<Expression<Func<SalesInquiry, bool>>>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<Func<IQueryable<SalesInquiry>, IQueryable<SalesInquiry>>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Expression<Func<SalesInquiry, bool>> predicate, int skip, int take,
                Func<IQueryable<SalesInquiry>, IQueryable<SalesInquiry>>? orderBy, CancellationToken _) =>
            {
                var query = rows.AsQueryable().Where(predicate);
                if (orderBy is not null) query = orderBy(query);
                return query.Skip(skip).Take(take).ToList();
            });

        var result = await _service.GetSalesInquiriesAsync(new SalesInquiryQuery(NewestFirst: true));

        result.IsSuccess.Should().BeTrue();
        result.Value!.Items.Select(i => i.Id).Should().Equal(freshClosed.Id, middleQuoted.Id, oldNew.Id);
    }

    [Fact]
    public async Task Admin_list_filters_by_status()
    {
        var now = DateTime.UtcNow;
        var rows = new List<SalesInquiry>
        {
            Lead(SalesInquiryConstants.Statuses.New, now.AddDays(-1)),
            Lead(SalesInquiryConstants.Statuses.Quoted, now),
        };

        Expression<Func<SalesInquiry, bool>>? captured = null;
        _repository
            .Setup(r => r.CountAsync(It.IsAny<Expression<Func<SalesInquiry, bool>>>(), It.IsAny<CancellationToken>()))
            .Callback((Expression<Func<SalesInquiry, bool>> p, CancellationToken _) => captured = p)
            .ReturnsAsync(1);
        _repository
            .Setup(r => r.GetPagedAsync(
                It.IsAny<Expression<Func<SalesInquiry, bool>>>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<Func<IQueryable<SalesInquiry>, IQueryable<SalesInquiry>>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SalesInquiry>());

        await _service.GetSalesInquiriesAsync(new SalesInquiryQuery(Status: "QUOTED", NewestFirst: true));

        captured.Should().NotBeNull();
        rows.AsQueryable().Where(captured!).Select(r => r.Status)
            .Should().Equal(SalesInquiryConstants.Statuses.Quoted);
    }

    // ── Status changes ───────────────────────────────────────

    [Fact]
    public async Task An_unknown_status_is_rejected_and_nothing_is_written()
    {
        var result = await _service.UpdateSalesInquiryStatusAsync(
            Guid.NewGuid(), new UpdateSalesInquiryStatusRequest("won"));

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_missing_lead_is_not_found()
    {
        _repository
            .Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SalesInquiry?)null);

        var result = await _service.UpdateSalesInquiryStatusAsync(
            Guid.NewGuid(), new UpdateSalesInquiryStatusRequest(SalesInquiryConstants.Statuses.Reviewing));

        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task Closing_a_lead_stamps_closed_at_once()
    {
        var lead = Lead(SalesInquiryConstants.Statuses.Quoted, DateTime.UtcNow.AddDays(-3));
        _repository
            .Setup(r => r.GetByIdAsync(lead.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(lead);

        var result = await _service.UpdateSalesInquiryStatusAsync(
            lead.Id, new UpdateSalesInquiryStatusRequest("Closed"));

        result.IsSuccess.Should().BeTrue();
        result.Value!.Status.Should().Be(SalesInquiryConstants.Statuses.Closed);
        lead.ClosedAt.Should().NotBeNull();
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
