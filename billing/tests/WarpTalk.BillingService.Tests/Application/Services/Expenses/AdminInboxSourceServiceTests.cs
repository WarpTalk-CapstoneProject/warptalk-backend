using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Contracts.Admin;

namespace WarpTalk.BillingService.Tests.Application.Services.Expenses;

/// <summary>G12: billing's pending-work inbox sources say what is waiting now, with stable keys and deep links.</summary>
public sealed class AdminInboxSourceServiceTests
{
    private static readonly DateTime Now = new(2026, 9, 25, 3, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Workspace = Guid.NewGuid();

    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly List<SalesInquiry> _leads = [];
    private readonly List<InboxInvoiceRow> _invoices = [];
    private readonly List<InboxSubscriptionRow> _subscriptions = [];
    private readonly List<ProviderCallStat> _calls = [];
    private readonly List<OperatingExpense> _planned = [];
    private readonly AdminInboxSourceService _service;

    public AdminInboxSourceServiceTests()
    {
        var leads = new Mock<ISalesInquiryRepository>();
        leads.Setup(r => r.GetPagedAsync(It.IsAny<Expression<Func<SalesInquiry, bool>>>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<Func<IQueryable<SalesInquiry>, IQueryable<SalesInquiry>>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Expression<Func<SalesInquiry, bool>> where, int _, int _, Func<IQueryable<SalesInquiry>, IQueryable<SalesInquiry>>? _, CancellationToken _)
                => _leads.AsQueryable().Where(where).ToList());
        var invoices = new Mock<IInvoiceRepository>();
        invoices.Setup(r => r.GetOutstandingForInboxAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => _invoices);
        var payments = new Mock<IPaymentRepository>();
        payments.Setup(r => r.GetDisputedSinceAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var subscriptions = new Mock<ISubscriptionRepository>();
        subscriptions.Setup(r => r.GetNeedingAttentionAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _subscriptions);
        var incidents = new Mock<IProviderStatusIncidentRepository>();
        incidents.Setup(r => r.GetUnresolvedSinceAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var calls = new Mock<IProviderCallStatRepository>();
        calls.Setup(r => r.GetRangeAsync(null, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => _calls);
        var expenses = new Mock<IOperatingExpenseRepository>();
        expenses.Setup(r => r.ListPlannedDueAsync(It.IsAny<DateOnly>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => _planned);

        _unitOfWork.SetupGet(u => u.SalesInquiryRepository).Returns(leads.Object);
        _unitOfWork.SetupGet(u => u.InvoiceRepository).Returns(invoices.Object);
        _unitOfWork.SetupGet(u => u.PaymentRepository).Returns(payments.Object);
        _unitOfWork.SetupGet(u => u.SubscriptionRepository).Returns(subscriptions.Object);
        _unitOfWork.SetupGet(u => u.ProviderStatusIncidents).Returns(incidents.Object);
        _unitOfWork.SetupGet(u => u.ProviderCallStats).Returns(calls.Object);
        _unitOfWork.SetupGet(u => u.OperatingExpenses).Returns(expenses.Object);

        var workspaces = new Mock<IWorkspaceClient>();
        workspaces.Setup(w => w.GetWorkspaceNamesAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new Dictionary<Guid, string> { [Workspace] = "Acme" }));

        _service = new AdminInboxSourceService(_unitOfWork.Object, workspaces.Object, NullLogger<AdminInboxSourceService>.Instance, new FixedTime(Now));
    }

    [Fact]
    public async Task Billing_items_cover_new_leads_past_due_and_bank_transfer_invoices_and_ending_trials()
    {
        _leads.Add(new SalesInquiry { Id = Guid.NewGuid(), FirstName = "An", LastName = "Le", Company = "Acme", WorkEmail = "an@acme.vn", RequestType = "enterprise", Status = "new", CreatedAt = Now.AddHours(-30) });
        _leads.Add(new SalesInquiry { Id = Guid.NewGuid(), FirstName = "Old", Status = "quoted", CreatedAt = Now.AddDays(-3) });
        _invoices.Add(new InboxInvoiceRow(Guid.NewGuid(), "INV-1", Workspace, 5_000_000m, "VND", Now.AddDays(-40), Now.AddDays(-20), "stripe"));
        _invoices.Add(new InboxInvoiceRow(Guid.NewGuid(), "INV-2", Workspace, 9_000_000m, "VND", Now.AddDays(-2), Now.AddDays(12), "internal_invoice"));
        _invoices.Add(new InboxInvoiceRow(Guid.NewGuid(), "INV-3", Workspace, 1m, "USD", Now.AddDays(-2), Now.AddDays(12), "stripe"));
        _subscriptions.Add(new InboxSubscriptionRow(Guid.NewGuid(), Workspace, "Pro", Now.AddDays(1), Now.AddDays(30), true, "healthy", null, Now));

        var response = await _service.GetBillingItemsAsync();

        response.Source.Should().Be(AdminInbox.Sources.Billing);
        response.Items.Select(i => i.Type).Should().BeEquivalentTo(
            [AdminInbox.Types.SalesLead, AdminInbox.Types.InvoicePastDue, AdminInbox.Types.InvoiceAwaitingPayment, AdminInbox.Types.TrialEnding]);
        var lead = response.Items.Single(i => i.Type == AdminInbox.Types.SalesLead);
        lead.Priority.Should().Be(AdminInbox.Priorities.High, "past its 24-hour SLA");
        lead.Href.Should().StartWith("/admin/sales-leads?status=new");
        var pastDue = response.Items.Single(i => i.Type == AdminInbox.Types.InvoicePastDue);
        pastDue.Priority.Should().Be(AdminInbox.Priorities.Urgent);
        pastDue.Customer.Should().Be("Acme");
        pastDue.NaturalCompletion.Should().BeTrue();
        response.Items.Single(i => i.Type == AdminInbox.Types.TrialEnding).NaturalCompletion.Should().BeFalse();
        response.Items[0].Priority.Should().Be(AdminInbox.Priorities.Urgent, "the most urgent come first");
    }

    [Fact]
    public async Task Quota_refusals_are_one_item_per_provider_and_day_and_planned_expenses_link_to_the_expense_page()
    {
        _calls.Add(new ProviderCallStat { Provider = "cartesia", HourStart = Now.AddHours(-3), Quota = 4 });
        _calls.Add(new ProviderCallStat { Provider = "cartesia", HourStart = Now.AddHours(-1), Quota = 2 });
        _calls.Add(new ProviderCallStat { Provider = "openai", HourStart = Now.AddHours(-1), Quota = 0 });
        _planned.Add(new OperatingExpense { Id = Guid.NewGuid(), Vendor = "Vietnix", Amount = 1_500_000m, Currency = "VND", Status = "planned", ExpenseDate = new DateOnly(2026, 9, 20), Category = new ExpenseCategory { Name = "Servers" } });

        var providers = await _service.GetProviderItemsAsync();
        var expenses = await _service.GetExpenseItemsAsync();

        providers.Items.Should().ContainSingle().Which.Key.Should().Be("provider_quota:cartesia:2026-09-25");
        providers.Items[0].Detail.Should().StartWith("6 calls");
        var due = expenses.Items.Should().ContainSingle().Subject;
        due.Priority.Should().Be(AdminInbox.Priorities.High, "it is overdue");
        due.Href.Should().Be("/admin/finance/expenses?q=Vietnix");
    }

    private sealed class FixedTime(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now, TimeSpan.Zero);
    }
}
