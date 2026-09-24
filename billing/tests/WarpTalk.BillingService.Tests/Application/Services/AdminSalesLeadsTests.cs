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

    private static SalesInquiry Lead(
        string status,
        DateTime createdAt,
        Guid? workspaceId = null,
        string company = "Analytical",
        string requestType = "enterprise",
        string source = SalesInquiryConstants.Sources.LandingPricing) => new()
    {
        Id = Guid.NewGuid(),
        FirstName = "Ada",
        LastName = "Lovelace",
        WorkEmail = "ada@example.com",
        Company = company,
        RequestType = requestType,
        Source = source,
        CurrentMonthlyMeetingVolume = "50",
        Status = status,
        WorkspaceId = workspaceId,
        CreatedAt = createdAt,
        UpdatedAt = createdAt,
    };

    /// <summary>Serves <paramref name="rows"/> through the real predicate and ordering the service builds.</summary>
    private void Serve(List<SalesInquiry> rows)
    {
        _repository
            .Setup(r => r.CountAsync(It.IsAny<Expression<Func<SalesInquiry, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Expression<Func<SalesInquiry, bool>> predicate, CancellationToken _) =>
                rows.AsQueryable().Count(predicate));
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
    }

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

    [Fact]
    public async Task Admin_list_filters_by_request_type_and_source_case_insensitively()
    {
        var now = DateTime.UtcNow;
        var enterprise = Lead(SalesInquiryConstants.Statuses.New, now, requestType: "enterprise");
        var demo = Lead(SalesInquiryConstants.Statuses.New, now.AddMinutes(-1), requestType: "demo", source: "in_app_upgrade");
        Serve([enterprise, demo]);

        var byType = await _service.GetSalesInquiriesAsync(new SalesInquiryQuery(NewestFirst: true, RequestType: "DEMO"));
        var bySource = await _service.GetSalesInquiriesAsync(new SalesInquiryQuery(NewestFirst: true, Source: "Landing_Pricing"));

        byType.Value!.Items.Select(i => i.Id).Should().Equal(demo.Id);
        byType.Value.TotalCount.Should().Be(1);
        bySource.Value!.Items.Select(i => i.Id).Should().Equal(enterprise.Id);
    }

    [Fact]
    public async Task Admin_list_created_window_is_inclusive_from_and_exclusive_to()
    {
        var anchor = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        var atFrom = Lead(SalesInquiryConstants.Statuses.New, anchor);
        var inside = Lead(SalesInquiryConstants.Statuses.New, anchor.AddDays(3));
        var atTo = Lead(SalesInquiryConstants.Statuses.New, anchor.AddDays(7));
        Serve([atFrom, inside, atTo]);

        var result = await _service.GetSalesInquiriesAsync(
            new SalesInquiryQuery(NewestFirst: true, CreatedFrom: anchor, CreatedTo: anchor.AddDays(7)));

        result.Value!.Items.Select(i => i.Id).Should().Equal(inside.Id, atFrom.Id);
    }

    [Fact]
    public async Task Admin_list_rejects_an_inverted_created_window()
    {
        var anchor = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

        var result = await _service.GetSalesInquiriesAsync(
            new SalesInquiryQuery(NewestFirst: true, CreatedFrom: anchor.AddDays(1), CreatedTo: anchor));

        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
        _repository.Verify(
            r => r.CountAsync(It.IsAny<Expression<Func<SalesInquiry, bool>>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData("created_asc")]
    [InlineData("company_asc")]
    [InlineData("company_desc")]
    [InlineData("created_desc")]
    public async Task Admin_list_honours_each_sort(string sort)
    {
        var now = DateTime.UtcNow;
        var zeta = Lead(SalesInquiryConstants.Statuses.New, now.AddDays(-2), company: "Zeta");
        var alpha = Lead(SalesInquiryConstants.Statuses.Closed, now, company: "Alpha");
        var mid = Lead(SalesInquiryConstants.Statuses.Quoted, now.AddDays(-1), company: "Mid");
        Serve([zeta, alpha, mid]);

        var result = await _service.GetSalesInquiriesAsync(new SalesInquiryQuery(NewestFirst: true, Sort: sort));

        var expected = sort switch
        {
            "created_asc" => new[] { zeta.Id, mid.Id, alpha.Id },
            "company_asc" => new[] { alpha.Id, mid.Id, zeta.Id },
            "company_desc" => new[] { zeta.Id, mid.Id, alpha.Id },
            _ => new[] { alpha.Id, mid.Id, zeta.Id },
        };
        result.Value!.Items.Select(i => i.Id).Should().Equal(expected);
    }

    [Fact]
    public async Task Admin_list_rejects_an_unknown_sort()
    {
        var result = await _service.GetSalesInquiriesAsync(new SalesInquiryQuery(NewestFirst: true, Sort: "value_desc"));

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
    }

    [Fact]
    public async Task Workspace_view_keeps_its_open_first_grouping_when_no_sort_is_sent()
    {
        var now = DateTime.UtcNow;
        var freshClosed = Lead(SalesInquiryConstants.Statuses.Closed, now);
        var oldNew = Lead(SalesInquiryConstants.Statuses.New, now.AddDays(-30));
        Serve([freshClosed, oldNew]);

        var result = await _service.GetSalesInquiriesAsync(new SalesInquiryQuery());

        result.Value!.Items.Select(i => i.Id).Should().Equal(oldNew.Id, freshClosed.Id);
    }

    [Fact]
    public async Task Controller_passes_every_inbox_parameter_through()
    {
        var service = new Mock<ISalesInquiryService>();
        SalesInquiryQuery? seen = null;
        service
            .Setup(s => s.GetSalesInquiriesAsync(It.IsAny<SalesInquiryQuery>(), It.IsAny<CancellationToken>()))
            .Callback<SalesInquiryQuery, CancellationToken>((q, _) => seen = q)
            .ReturnsAsync(Result.Failure<PaginatedResponse<SalesInquiryDto>>("bad sort", ErrorCodes.ValidationError));
        var controller = new AdminSalesLeadsController(
            service.Object,
            Mock.Of<Microsoft.Extensions.Logging.ILogger<AdminSalesLeadsController>>());
        var from = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        var workspaceId = Guid.NewGuid();

        var response = await controller.GetLeads(
            page: 2, pageSize: 10, status: "new", search: "acme", workspaceId: workspaceId,
            requestType: "enterprise", source: "landing_pricing", createdFrom: from, createdTo: from.AddDays(1),
            sort: "company_asc");

        response.Should().BeOfType<BadRequestObjectResult>();
        seen.Should().Be(new SalesInquiryQuery(
            2, 10, "new", "acme", workspaceId, NewestFirst: true, RequestType: "enterprise",
            Source: "landing_pricing", CreatedFrom: from, CreatedTo: from.AddDays(1), Sort: "company_asc"));
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
