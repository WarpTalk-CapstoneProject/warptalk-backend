using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Application.Services.Expenses;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Contracts.Admin;

namespace WarpTalk.BillingService.Tests.Application.Services.Expenses;

/// <summary>
/// G12 operating expenses against in-memory repositories: validation, FX conversion at each expense's
/// own date, recurrence generation, the report, the P&amp;L with expenses and the CSV import.
/// </summary>
public sealed class OperatingExpenseServiceTests
{
    private static readonly DateTime Now = new(2026, 9, 25, 3, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Actor = Guid.NewGuid();

    private readonly List<ExpenseCategory> _categories = [];
    private readonly List<OperatingExpense> _expenses = [];
    private readonly List<ExpenseBudget> _budgets = [];
    private readonly List<FxRate> _rates = [];
    private readonly Mock<IAdminBillingInsightsService> _insights = new();
    private readonly Mock<IExpenseReceiptStorage> _receipts = new();
    private readonly OperatingExpenseService _service;
    private readonly ExpenseCategory _saas;
    private readonly ExpenseCategory _servers;
    private readonly ExpenseCategory _retired;

    public OperatingExpenseServiceTests()
    {
        _saas = AddCategory("saas", "SaaS subscriptions");
        _servers = AddCategory("servers", "Servers & hosting");
        _retired = AddCategory("old", "Old stuff", active: false);

        _rates.Add(new FxRate { BaseCurrency = "USD", QuoteCurrency = "VND", RateDate = new DateOnly(2026, 8, 1), Rate = 25_000m, Source = "stripe_fx_quote", FetchedAt = Now });
        _rates.Add(new FxRate { BaseCurrency = "USD", QuoteCurrency = "VND", RateDate = new DateOnly(2026, 9, 1), Rate = 26_000m, Source = "stripe_fx_quote", FetchedAt = Now });

        var categories = new Mock<IExpenseCategoryRepository>();
        categories.Setup(r => r.ListOrderedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => _categories.OrderBy(c => c.SortOrder).ToList());
        categories.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => _categories.FirstOrDefault(c => c.Id == id));
        categories.Setup(r => r.GetBySlugAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string slug, CancellationToken _) => _categories.FirstOrDefault(c => c.Slug == slug));
        categories.Setup(r => r.IsInUseAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => _expenses.Any(e => e.CategoryId == id));
        categories.Setup(r => r.AddAsync(It.IsAny<ExpenseCategory>(), It.IsAny<CancellationToken>()))
            .Callback<ExpenseCategory, CancellationToken>((c, _) => _categories.Add(c)).Returns(Task.CompletedTask);

        var expenses = new Mock<IOperatingExpenseRepository>();
        IEnumerable<OperatingExpense> Live() => _expenses.Where(e => e.DeletedAt == null).Select(WithCategory);
        expenses.Setup(r => r.ListInRangeAsync(It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateOnly from, DateOnly to, CancellationToken _) => Live().Where(e => e.ExpenseDate >= from && e.ExpenseDate <= to).ToList());
        expenses.Setup(r => r.GetLiveAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => Live().FirstOrDefault(e => e.Id == id));
        expenses.Setup(r => r.ListSeriesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Live().Where(e => e.Recurrence != "none").ToList());
        expenses.Setup(r => r.ListSeriesDueAsync(It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateOnly dueBy, CancellationToken _) => Live().Where(e => e.Recurrence != "none" && e.NextDueDate <= dueBy).ToList());
        expenses.Setup(r => r.OccurrenceExistsAsync(It.IsAny<Guid>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid series, DateOnly date, CancellationToken _) => _expenses.Any(e => e.RecurringSourceId == series && e.ExpenseDate == date));
        expenses.Setup(r => r.ListPlannedDueAsync(It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateOnly dueBy, CancellationToken _) => Live().Where(e => e.Status == "planned" && e.ExpenseDate <= dueBy).ToList());
        expenses.Setup(r => r.AddAsync(It.IsAny<OperatingExpense>(), It.IsAny<CancellationToken>()))
            .Callback<OperatingExpense, CancellationToken>((e, _) => _expenses.Add(e)).Returns(Task.CompletedTask);

        var budgets = new Mock<IExpenseBudgetRepository>();
        budgets.Setup(r => r.ListInRangeAsync(It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateOnly from, DateOnly to, CancellationToken _) => _budgets.Where(b => b.Month >= from && b.Month <= to).ToList());
        budgets.Setup(r => r.GetAsync(It.IsAny<Guid>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid c, DateOnly m, CancellationToken _) => _budgets.FirstOrDefault(b => b.CategoryId == c && b.Month == m));
        budgets.Setup(r => r.AddAsync(It.IsAny<ExpenseBudget>(), It.IsAny<CancellationToken>()))
            .Callback<ExpenseBudget, CancellationToken>((b, _) => _budgets.Add(b)).Returns(Task.CompletedTask);
        budgets.Setup(r => r.Remove(It.IsAny<ExpenseBudget>())).Callback<ExpenseBudget>(b => _budgets.Remove(b));

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.ExpenseCategories).Returns(categories.Object);
        unitOfWork.SetupGet(u => u.OperatingExpenses).Returns(expenses.Object);
        unitOfWork.SetupGet(u => u.ExpenseBudgets).Returns(budgets.Object);
        unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var fx = new Mock<IFxRateService>();
        fx.Setup(f => f.GetTableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => FxRateTable.From(_rates, 26_300m));

        _service = new OperatingExpenseService(
            unitOfWork.Object, fx.Object, _receipts.Object, _insights.Object,
            NullLogger<OperatingExpenseService>.Instance, new FixedTime(Now));
    }

    private ExpenseCategory AddCategory(string slug, string name, bool active = true)
    {
        var category = new ExpenseCategory { Id = Guid.NewGuid(), Slug = slug, Name = name, IsActive = active, SortOrder = _categories.Count };
        _categories.Add(category);
        return category;
    }

    private OperatingExpense WithCategory(OperatingExpense expense)
    {
        expense.Category = _categories.FirstOrDefault(c => c.Id == expense.CategoryId);
        return expense;
    }

    private static SaveOperatingExpenseRequest Request(
        Guid category, decimal amount, string currency = "VND", DateOnly? date = null, string? status = null,
        string? recurrence = null, string vendor = "GitHub")
        => new(date ?? new DateOnly(2026, 9, 10), vendor, category, null, amount, currency, "company_card", status, "Tú",
            ["infra", "Infra"], recurrence, null);

    // ── Validation ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_vnd_amount_with_decimals_and_a_retired_category_are_refused()
    {
        (await _service.CreateExpenseAsync(Request(_saas.Id, 10.5m), Actor)).ErrorCode.Should().Be(ErrorCodes.ValidationError);
        (await _service.CreateExpenseAsync(Request(_retired.Id, 100m), Actor)).Error.Should().Contain("retired");
        (await _service.CreateExpenseAsync(Request(_saas.Id, 100m, "EUR"), Actor)).Error.Should().Contain("VND or USD");
        _expenses.Should().BeEmpty();
    }

    [Fact]
    public async Task A_usd_expense_is_converted_at_the_rate_of_its_own_date()
    {
        var august = await _service.CreateExpenseAsync(Request(_saas.Id, 10m, "USD", new DateOnly(2026, 8, 15)), Actor);
        var september = await _service.CreateExpenseAsync(Request(_saas.Id, 10m, "USD", new DateOnly(2026, 9, 15)), Actor);

        august.Value!.AmountVnd.Should().Be(250_000m);
        september.Value!.AmountVnd.Should().Be(260_000m);
        september.Value.FxRate.Should().Be(26_000m);
        september.Value.Tags.Should().Equal("infra");
        september.Value.PaidAt.Should().NotBeNull("recorded as paid by default, on its own date");
    }

    // ── Recurrence ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_monthly_series_writes_its_next_occurrence_as_planned_once()
    {
        var created = await _service.CreateExpenseAsync(
            Request(_servers.Id, 1_500_000m, date: new DateOnly(2026, 9, 28), recurrence: "monthly", vendor: "Vietnix"), Actor);
        created.Value!.NextDueDate.Should().Be(new DateOnly(2026, 10, 28));

        // Today + 7 days lead = Oct 2: not due yet.
        (await _service.GenerateDueOccurrencesAsync(new DateOnly(2026, 9, 25))).Should().Be(0);

        (await _service.GenerateDueOccurrencesAsync(new DateOnly(2026, 10, 22))).Should().Be(1);
        (await _service.GenerateDueOccurrencesAsync(new DateOnly(2026, 10, 22))).Should().Be(0, "the series advanced");

        var occurrence = _expenses.Single(e => e.RecurringSourceId == created.Value.Id);
        occurrence.Status.Should().Be("planned");
        occurrence.ExpenseDate.Should().Be(new DateOnly(2026, 10, 28));
        occurrence.Recurrence.Should().Be("none");
        _expenses.Single(e => e.Id == created.Value.Id).NextDueDate.Should().Be(new DateOnly(2026, 11, 28));
    }

    [Fact]
    public async Task Marking_a_planned_expense_paid_records_who_and_when()
    {
        var planned = await _service.CreateExpenseAsync(Request(_saas.Id, 300_000m, status: "planned"), Actor);
        planned.Value!.PaidAt.Should().BeNull();

        var paid = await _service.MarkPaidAsync(planned.Value.Id, new MarkExpensePaidRequest(null, "Nhi", "bank_transfer"), Actor);

        paid.Value!.Status.Should().Be("paid");
        paid.Value.PaidBy.Should().Be("Nhi");
        paid.Value.PaidAt.Should().Be(Now);
    }

    // ── Report ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_report_totals_by_month_flags_over_budget_and_projects_commitments()
    {
        await _service.CreateExpenseAsync(Request(_saas.Id, 2_000_000m, date: new DateOnly(2026, 9, 3)), Actor);
        await _service.CreateExpenseAsync(Request(_saas.Id, 10m, "USD", new DateOnly(2026, 8, 3), vendor: "Linear"), Actor);
        await _service.CreateExpenseAsync(Request(_servers.Id, 1_000_000m, date: new DateOnly(2026, 9, 20), recurrence: "monthly", vendor: "Vietnix"), Actor);
        await _service.SaveBudgetsAsync(new SaveExpenseBudgetsRequest([new ExpenseBudgetInput(_saas.Id, "2026-09", 1_500_000m, null)]), Actor);

        var report = (await _service.GetReportAsync("2026-08", "2026-09")).Value!;

        report.Months.Select(m => m.TotalVnd).Should().Equal(250_000m, 3_000_000m);
        var september = report.Months[1];
        september.Categories.Single(c => c.CategoryId == _saas.Id).OverBudget.Should().BeTrue();
        report.BudgetAlerts.Should().ContainSingle(a => a.Over && a.Month == "2026-09" && a.Percent == 133.3m);
        report.TopVendors[0].Vendor.Should().Be("GitHub");
        report.RecurringMonthlyRunRateVnd.Should().Be(1_000_000m);
        // Oct 20, Nov 20 and Dec 20 fall within 90 days of Sep 25.
        report.Commitments.Where(c => c.Projected).Select(c => c.DueDate)
            .Should().Equal(new DateOnly(2026, 10, 20), new DateOnly(2026, 11, 20), new DateOnly(2026, 12, 20));
    }

    [Fact]
    public async Task The_pnl_subtracts_operating_expenses_from_gross_margin_per_month()
    {
        await _service.CreateExpenseAsync(Request(_saas.Id, 1_000_000m, date: new DateOnly(2026, 9, 3)), Actor);
        _insights.Setup(i => i.GetProfitAndLossAsync(It.IsAny<AdminInsightsQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(Pnl(
                Day("2026-08-10", revenue: 5_000_000m, cost: 1_000_000m),
                Day("2026-09-10", revenue: 3_000_000m, cost: 500_000m),
                Day("2026-09-11", revenue: 1_000_000m, cost: null))));

        var pnl = (await _service.GetProfitAndLossAsync("2026-08", "2026-09", "Asia/Ho_Chi_Minh")).Value!;

        pnl.Months[0].NetResult.Should().Be(4_000_000m);
        var september = pnl.Months[1];
        september.Revenue.Should().Be(4_000_000m);
        september.AiCost.Should().Be(500_000m, "a day without a reconstructable cost is left out, and the P&L's note says so");
        september.GrossMargin.Should().Be(3_500_000m);
        september.OperatingExpenses.Should().Be(1_000_000m);
        september.NetResult.Should().Be(2_500_000m);
        september.NetMarginPercent.Should().Be(62.5m);
        pnl.Total.NetResult.Should().Be(6_500_000m);
    }

    private static AdminPnlPeriodDto Day(string key, decimal? revenue, decimal? cost)
        => new(key, revenue, cost, 0m, null, null, 100, 100m, 1, null, 26_000m, []);

    private static AdminProfitAndLossDto Pnl(params AdminPnlPeriodDto[] days)
        => new(new AdminInsightRange(Now, Now), new AdminInsightRange(Now, Now), Now, [], 0m, 100m, null, null,
            days, [], [], [], [], null);

    // ── Import ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Import_previews_every_row_refuses_errors_and_commits_only_valid_rows_when_asked()
    {
        await _service.CreateExpenseAsync(Request(_saas.Id, 300_000m, date: new DateOnly(2026, 9, 1), vendor: "Canva"), Actor);
        const string csv = "date,vendor,category,amount,currency,status\n"
                           + "2026-09-01,Canva,saas,300000,VND,paid\n"
                           + "2026-09-02,OpenAI,provider_plans,50,USD,paid\n"
                           + "2026-09-03,Vietnix,Servers & hosting,\"1,500,000\",,planned\n";

        var preview = (await _service.PreviewImportAsync(new ExpenseImportRequest(csv))).Value!;
        preview.ValidCount.Should().Be(2);
        preview.Rows[0].Warnings.Should().ContainSingle(w => w.Contains("already recorded"));
        preview.Rows[1].Errors.Should().ContainSingle(e => e.Contains("not a known category"));
        preview.Rows[2].CategoryId.Should().Be(_servers.Id, "a category matches by name as well as slug");

        var refused = await _service.ImportAsync(new ExpenseImportRequest(csv), Actor);
        refused.ErrorCode.Should().Be(ErrorCodes.ValidationError);
        _expenses.Should().HaveCount(1);

        var imported = (await _service.ImportAsync(new ExpenseImportRequest(csv, SkipInvalid: true), Actor)).Value!;
        imported.Imported.Should().Be(2);
        imported.Skipped.Should().Be(1);
        _expenses.Where(e => e.ImportBatchId == imported.BatchId).Should().HaveCount(2);
    }

    [Fact]
    public async Task Import_without_the_required_columns_says_which_are_missing()
    {
        var result = await _service.PreviewImportAsync(new ExpenseImportRequest("vendor,amount\nX,1\n"));

        result.Error.Should().Contain("date").And.Contain("category");
    }

    private sealed class FixedTime(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now, TimeSpan.Zero);
    }
}
