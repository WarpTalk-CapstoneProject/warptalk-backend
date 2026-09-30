using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Persistence;
using WarpTalk.BillingService.Infrastructure.Repositories;
using WarpTalk.Shared;
using Xunit;

namespace WarpTalk.BillingService.Tests.Application.Services;

/// <summary>
/// GET api/v1/invoices/global: search, status, workspace, currency, the issued-at window, the
/// total range and the sort keys. Service validation with a mocked repository; the repository's
/// WHERE/ORDER BY over plain rows; and Npgsql translation via ToQueryString — no Docker needed.
/// </summary>
public class InvoiceServiceGlobalInvoicesTests
{
    private static readonly DateTime Anchor = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IInvoiceRepository> _invoices = new();
    private readonly InvoiceService _service;
    private GlobalInvoiceFilter? _seen;

    public InvoiceServiceGlobalInvoicesTests()
    {
        _unitOfWork.Setup(u => u.InvoiceRepository).Returns(_invoices.Object);
        _invoices
            .Setup(r => r.GetGlobalPageAsync(It.IsAny<GlobalInvoiceFilter>(), It.IsAny<CancellationToken>()))
            .Callback<GlobalInvoiceFilter, CancellationToken>((f, _) => _seen = f)
            .ReturnsAsync(new PagedResult<Invoice>(Array.Empty<Invoice>(), 0, 1, 20));

        _service = new InvoiceService(
            _unitOfWork.Object,
            Mock.Of<ILogger<InvoiceService>>(),
            Mock.Of<IStripePaymentService>(),
            Mock.Of<IWorkspaceClient>());
    }

    // ── Service validation ───────────────────────────────────

    public static TheoryData<GlobalInvoiceQuery> InvalidQueries => new()
    {
        new GlobalInvoiceQuery { Status = "overdue" },
        new GlobalInvoiceQuery { Sort = "number_asc" },
        new GlobalInvoiceQuery { Currency = "DONG" },
        new GlobalInvoiceQuery { Currency = "U5D" },
        new GlobalInvoiceQuery { FromDate = Anchor.AddDays(1), ToDate = Anchor },
        new GlobalInvoiceQuery { MinTotal = 100m, MaxTotal = 10m },
    };

    [Theory]
    [MemberData(nameof(InvalidQueries))]
    public async Task Invalid_input_is_a_validation_error_and_reads_nothing(GlobalInvoiceQuery query)
    {
        var result = await _service.GetGlobalInvoicesAsync(query);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
        _invoices.Verify(
            r => r.GetGlobalPageAsync(It.IsAny<GlobalInvoiceFilter>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Defaults_are_the_old_page_with_no_filter_and_the_old_order()
    {
        var result = await _service.GetGlobalInvoicesAsync(new GlobalInvoiceQuery());

        result.IsSuccess.Should().BeTrue(result.Error);
        _seen.Should().Be(new GlobalInvoiceFilter(new PageRequest(1, 20)));
        _seen!.Sort.Should().Be(GlobalInvoiceSorts.IssuedDesc);
    }

    [Fact]
    public async Task Every_value_reaches_the_repository_normalized()
    {
        var workspaceId = Guid.NewGuid();

        var result = await _service.GetGlobalInvoicesAsync(new GlobalInvoiceQuery
        {
            PageNumber = 3,
            PageSize = 50,
            Search = "  INV-2026  ",
            Status = "PAID",
            WorkspaceId = workspaceId,
            Currency = "usd",
            FromDate = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Unspecified),
            ToDate = Anchor.AddDays(31),
            MinTotal = 10m,
            MaxTotal = 10m,
            Sort = "TOTAL_DESC",
        });

        result.IsSuccess.Should().BeTrue(result.Error);
        _seen.Should().Be(new GlobalInvoiceFilter(
            new PageRequest(3, 50), "INV-2026", "paid", workspaceId, "USD",
            Anchor, Anchor.AddDays(31), 10m, 10m, GlobalInvoiceSorts.TotalDesc));
        _seen!.FromDate!.Value.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Theory]
    [InlineData("all")]
    [InlineData("issued")]
    [InlineData("uncollectible")]
    public async Task All_and_every_stored_status_are_accepted(string status)
    {
        var result = await _service.GetGlobalInvoicesAsync(new GlobalInvoiceQuery { Status = status });

        result.IsSuccess.Should().BeTrue(result.Error);
        _seen!.Status.Should().Be(status == "all" ? null : status);
    }

    // ── Repository WHERE / ORDER BY over plain rows ──────────

    private static readonly Guid WorkspaceA = Guid.NewGuid();

    private static Invoice NewInvoice(
        string number,
        decimal total,
        DateTime issuedAt,
        string status = "paid",
        string currency = "VND",
        DateTime? dueAt = null,
        Guid? workspaceId = null) => new()
    {
        Id = Guid.NewGuid(),
        InvoiceNumber = number,
        Total = total,
        Currency = currency,
        Status = status,
        IssuedAt = issuedAt,
        CreatedAt = issuedAt,
        DueAt = dueAt,
        LineItems = "[]",
        Payment = new Payment { Subscription = new Subscription { WorkspaceId = workspaceId ?? Guid.NewGuid() } },
    };

    private static readonly Invoice First = NewInvoice("INV-001", 100m, Anchor, dueAt: Anchor.AddDays(20), workspaceId: WorkspaceA);
    private static readonly Invoice Second = NewInvoice("INV-002", 900m, Anchor.AddDays(5), status: "open", currency: "usd", dueAt: Anchor.AddDays(10));
    private static readonly Invoice Third = NewInvoice("INV-003", 500m, Anchor.AddDays(10));

    private static List<Guid> Filter(GlobalInvoiceFilter filter) =>
        InvoiceRepository.ApplyGlobalFilters(new[] { First, Second, Third }.AsQueryable(), filter)
            .Select(i => i.Id)
            .ToList();

    private static List<Guid> Sort(string sort) =>
        InvoiceRepository.ApplyGlobalSort(new[] { First, Second, Third }.AsQueryable(), sort)
            .Select(i => i.Id)
            .ToList();

    private static GlobalInvoiceFilter Page => new(new PageRequest(1, 20));

    [Fact]
    public void Each_filter_narrows_the_rows()
    {
        Filter(Page with { Search = Second.Id.ToString() }).Should().Equal(Second.Id);
        Filter(Page with { Status = "open" }).Should().Equal(Second.Id);
        Filter(Page with { WorkspaceId = WorkspaceA }).Should().Equal(First.Id);
        Filter(Page with { Currency = "USD" }).Should().Equal(Second.Id);
        Filter(Page with { MinTotal = 500m, MaxTotal = 900m }).Should().Equal(Second.Id, Third.Id);
        Filter(Page).Should().HaveCount(3);
    }

    [Fact]
    public void The_issued_window_is_inclusive_from_and_exclusive_to()
    {
        Filter(Page with { FromDate = Anchor, ToDate = Anchor.AddDays(10) }).Should().Equal(First.Id, Second.Id);
    }

    [Fact]
    public void Each_sort_orders_the_rows()
    {
        Sort(GlobalInvoiceSorts.IssuedDesc).Should().Equal(Third.Id, Second.Id, First.Id);
        Sort(GlobalInvoiceSorts.IssuedAsc).Should().Equal(First.Id, Second.Id, Third.Id);
        Sort(GlobalInvoiceSorts.TotalDesc).Should().Equal(Second.Id, Third.Id, First.Id);
        Sort(GlobalInvoiceSorts.TotalAsc).Should().Equal(First.Id, Third.Id, Second.Id);
        // Third has no due date and goes last.
        Sort(GlobalInvoiceSorts.DueAsc).Should().Equal(Second.Id, First.Id, Third.Id);
    }

    // ── Translation ──────────────────────────────────────────

    private static BillingDbContext Context() =>
        new(new DbContextOptionsBuilder<BillingDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused").Options);

    [Fact]
    public void Every_filter_translates_to_sql()
    {
        using var context = Context();
        var filter = new GlobalInvoiceFilter(
            new PageRequest(1, 20), "INV-2026", "paid", Guid.NewGuid(), "USD",
            Anchor, Anchor.AddDays(30), 1m, 1000m, GlobalInvoiceSorts.DueAsc);

        var sql = InvoiceRepository
            .ApplyGlobalSort(InvoiceRepository.ApplyGlobalFilters(context.Invoices, filter), filter.Sort)
            .ToQueryString();

        sql.Should().Contain("invoice_number ILIKE @")
            .And.Contain("upper(")
            .And.Contain("issued_at >= @")
            .And.Contain("issued_at < @")
            .And.Contain("total >= @")
            .And.Contain("total <= @")
            .And.Contain("workspace_id = @")
            .And.Contain("ORDER BY");
    }

    [Fact]
    public void A_guid_search_is_an_id_match_not_a_text_match()
    {
        using var context = Context();

        var sql = InvoiceRepository
            .ApplyGlobalFilters(context.Invoices, Page with { Search = Guid.NewGuid().ToString() })
            .ToQueryString();

        sql.Should().NotContain("ILIKE").And.MatchRegex(@"\w+\.id = @");
    }

    [Fact]
    public void The_default_sort_is_created_at_descending()
    {
        using var context = Context();

        var sql = InvoiceRepository.ApplyGlobalSort(context.Invoices, GlobalInvoiceSorts.IssuedDesc).ToQueryString();

        sql.Should().MatchRegex(@"ORDER BY \w+\.created_at DESC, \w+\.id DESC");
    }
}
