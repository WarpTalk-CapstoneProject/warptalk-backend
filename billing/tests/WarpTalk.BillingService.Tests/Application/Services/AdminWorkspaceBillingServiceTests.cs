using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Entitlements;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared.Events;
using Xunit;

namespace WarpTalk.BillingService.Tests.Application.Services;

/// <summary>
/// The admin workspace page's money actions. The property every test here protects is the one the
/// owner asked for: an action that cannot be recorded in the platform audit log is not made, and
/// one that is made carries the reason and the workspace it was made on.
/// </summary>
public class AdminWorkspaceBillingServiceTests
{
    private static readonly DateTime Now = new(2026, 9, 24, 8, 0, 0, DateTimeKind.Utc);

    private readonly Guid _workspaceId = Guid.NewGuid();
    private readonly AdminActorContext _actor = new(Guid.NewGuid(), "corr-1");

    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<ISubscriptionRepository> _subscriptions = new();
    private readonly Mock<ICreditTransactionRepository> _transactions = new();
    private readonly Mock<IPlanRepository> _plans = new();
    private readonly Mock<IInvoiceRepository> _invoices = new();
    private readonly Mock<IAdminAuditRecorder> _audit = new();
    private readonly Mock<IEntitlementResolver> _resolver = new();
    private readonly Mock<IUsageRateCardRepository> _pricing = new();

    /// <summary>What happened, in order: "audit:succeeded", "save", "audit:failed".</summary>
    private readonly List<string> _calls = new();

    private readonly AdminWorkspaceBillingService _service;

    public AdminWorkspaceBillingServiceTests()
    {
        _unitOfWork.SetupGet(u => u.SubscriptionRepository).Returns(_subscriptions.Object);
        _unitOfWork.SetupGet(u => u.CreditTransactionRepository).Returns(_transactions.Object);
        _unitOfWork.SetupGet(u => u.Plans).Returns(_plans.Object);
        _unitOfWork.SetupGet(u => u.InvoiceRepository).Returns(_invoices.Object);
        _unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Callback(() => _calls.Add("save"))
            .ReturnsAsync(1);

        AuditReturns(Result.Success());

        _resolver.Setup(r => r.ResolveAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => new WorkspaceEntitlementMap(
                id, "pro", true, Now, new List<ResolvedEntitlement>
                {
                    ResolvedEntitlement.Number(EntitlementConstants.Keys.MaxLanguages, 3, "plan:pro"),
                }));

        var creditService = new CreditService(
            _unitOfWork.Object,
            NullLogger<CreditService>.Instance,
            new Mock<IUsageSettlementService>().Object,
            new Mock<IWorkspaceClient>().Object);

        _service = new AdminWorkspaceBillingService(
            _unitOfWork.Object,
            creditService,
            _resolver.Object,
            _pricing.Object,
            _audit.Object,
            NullLogger<AdminWorkspaceBillingService>.Instance,
            new FixedClock(Now));
    }

    private void AuditReturns(Result result) =>
        _audit
            .Setup(a => a.RecordAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<Guid>(), It.IsAny<Guid>(),
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, string?>?>(), It.IsAny<IReadOnlyDictionary<string, string?>?>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback((string _, string _, Guid? _, Guid _, Guid _, string _, string _,
                IReadOnlyDictionary<string, string?>? _, IReadOnlyDictionary<string, string?>? _, bool succeeded, CancellationToken _)
                => _calls.Add(succeeded ? "audit:succeeded" : "audit:failed"))
            .ReturnsAsync(result);

    private Subscription GivenSubscription(Action<Subscription>? configure = null)
    {
        var plan = new Plan { Id = Guid.NewGuid(), Name = "Pro", Slug = "pro", CreditsPerCycle = 1000, IsActive = true };
        var subscription = new Subscription
        {
            Id = Guid.NewGuid(),
            WorkspaceId = _workspaceId,
            UserId = Guid.NewGuid(),
            PlanId = plan.Id,
            Plan = plan,
            CreditsRemaining = 100,
            IsActive = true,
            Status = SubscriptionConstants.SubscriptionStatuses.Active,
            CurrentPeriodStart = Now.AddDays(-10),
            CurrentPeriodEnd = Now.AddDays(20),
        };
        configure?.Invoke(subscription);

        _subscriptions
            .Setup(r => r.GetActiveByWorkspaceIdAsync(_workspaceId, It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(subscription);
        _subscriptions
            .Setup(r => r.GetByIdAsync(subscription.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(subscription);
        return subscription;
    }

    // ── adjust credits: wired to CreditService, audited first ───────────────────────────────

    [Fact]
    public async Task Adjust_records_the_action_before_it_saves_and_books_the_ledger_row_on_the_workspace()
    {
        var subscription = GivenSubscription();
        CreditTransaction? booked = null;
        _transactions.Setup(r => r.AddAsync(It.IsAny<CreditTransaction>(), It.IsAny<CancellationToken>()))
            .Callback<CreditTransaction, CancellationToken>((tx, _) => booked = tx)
            .Returns(Task.CompletedTask);

        var result = await _service.AdjustCreditsAsync(
            _workspaceId, new AdminAdjustWorkspaceCreditsRequest(250, "Outage on 23 Sep"), _actor);

        result.IsSuccess.Should().BeTrue(result.Error);
        _calls.Should().Equal("audit:succeeded", "save");
        subscription.CreditsRemaining.Should().Be(350);
        booked!.WorkspaceId.Should().Be(_workspaceId, "the admin ledger is filtered on workspace_id");
        booked.UserId.Should().Be(_actor.ActorId);
        result.Value!.LedgerEntry!.Amount.Should().Be(250);
        result.Value.LedgerEntry.BalanceAfter.Should().Be(350);
        _audit.Verify(a => a.RecordAsync(
            AdminAuditWorkspaceActions.CreditAdjusted, AdminAuditEntityTypes.CreditAdjustment, booked.Id, _workspaceId,
            _actor.ActorId, "Outage on 23 Sep", "corr-1",
            It.IsAny<IReadOnlyDictionary<string, string?>?>(), It.IsAny<IReadOnlyDictionary<string, string?>?>(),
            true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Adjust_is_not_saved_when_the_audit_log_refuses_it()
    {
        GivenSubscription();
        AuditReturns(Result.Failure("audit log unreachable", ErrorCodes.InternalServerError));

        var result = await _service.AdjustCreditsAsync(
            _workspaceId, new AdminAdjustWorkspaceCreditsRequest(250, "Outage"), _actor);

        result.IsSuccess.Should().BeFalse();
        _calls.Should().Equal("audit:succeeded");
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        _unitOfWork.Verify(u => u.ClearTracking(), Times.AtLeastOnce);
    }

    [Theory]
    [InlineData(0, "reason")]
    [InlineData(1_000_001, "reason")]
    [InlineData(-1_000_001, "reason")]
    [InlineData(10, "   ")]
    public async Task Adjust_refuses_a_bad_amount_or_a_missing_reason_without_recording_anything(int amount, string reason)
    {
        GivenSubscription();

        var result = await _service.AdjustCreditsAsync(
            _workspaceId, new AdminAdjustWorkspaceCreditsRequest(amount, reason), _actor);

        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
        _calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Adjust_that_would_go_below_zero_is_a_conflict()
    {
        GivenSubscription();

        var result = await _service.AdjustCreditsAsync(
            _workspaceId, new AdminAdjustWorkspaceCreditsRequest(-101, "Clawback"), _actor);

        result.ErrorCode.Should().Be(ErrorCodes.Conflict);
        _calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_save_that_fails_after_the_record_is_followed_by_a_failed_entry()
    {
        GivenSubscription();
        _unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Callback(() => _calls.Add("save"))
            .ThrowsAsync(new InvalidOperationException("concurrency"));

        var result = await _service.AdjustCreditsAsync(
            _workspaceId, new AdminAdjustWorkspaceCreditsRequest(5, "Goodwill"), _actor);

        result.IsSuccess.Should().BeFalse();
        _calls.Should().Equal("audit:succeeded", "save", "audit:failed");
        _audit.Verify(a => a.RecordAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<Guid>(), It.IsAny<Guid>(),
            It.IsAny<string>(), "corr-1:failed",
            It.IsAny<IReadOnlyDictionary<string, string?>?>(), It.IsAny<IReadOnlyDictionary<string, string?>?>(),
            false, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── trial and comp ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Extend_trial_moves_the_trial_and_its_period_and_lifts_a_trial_ended_suspension()
    {
        var subscription = GivenSubscription(s =>
        {
            s.TrialEndsAt = Now.AddDays(-2);
            s.CurrentPeriodEnd = Now.AddDays(-2);
            s.ServiceState = SubscriptionConstants.ServiceStates.Suspended;
            s.SuspendedReason = SubscriptionConstants.SuspendedReasons.TrialEnded;
        });

        var result = await _service.ExtendTrialAsync(_workspaceId, new AdminExtendTrialRequest(14, "Pilot running late"), _actor);

        result.IsSuccess.Should().BeTrue(result.Error);
        subscription.TrialEndsAt.Should().Be(Now.AddDays(14));
        subscription.CurrentPeriodEnd.Should().Be(Now.AddDays(14), "the paywall reads CurrentPeriodEnd");
        subscription.ServiceState.Should().Be(SubscriptionConstants.ServiceStates.Healthy);
        _calls.Should().Equal("audit:succeeded", "save");
    }

    [Fact]
    public async Task Extend_trial_on_a_paid_subscription_is_refused()
    {
        GivenSubscription();

        var result = await _service.ExtendTrialAsync(_workspaceId, new AdminExtendTrialRequest(7, "x"), _actor);

        result.ErrorCode.Should().Be(ErrorCodes.Conflict);
        _calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Comp_extends_the_paid_through_date_and_grants_the_periods_credits_with_no_invoice()
    {
        var subscription = GivenSubscription();
        var periodEnd = subscription.CurrentPeriodEnd;
        CreditTransaction? booked = null;
        _transactions.Setup(r => r.AddAsync(It.IsAny<CreditTransaction>(), It.IsAny<CancellationToken>()))
            .Callback<CreditTransaction, CancellationToken>((tx, _) => booked = tx)
            .Returns(Task.CompletedTask);

        var result = await _service.CompPeriodAsync(_workspaceId, new AdminCompPeriodRequest(2, "Churn save"), _actor);

        result.IsSuccess.Should().BeTrue(result.Error);
        subscription.CurrentPeriodEnd.Should().Be(periodEnd.AddMonths(2));
        subscription.CreditsRemaining.Should().Be(100 + 2 * 1000);
        booked!.Amount.Should().Be(2000);
        booked.WorkspaceId.Should().Be(_workspaceId);
        _invoices.Verify(r => r.AddAsync(It.IsAny<Invoice>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── entitlement overrides ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Entitlement_overrides_write_the_contract_layer_and_null_clears_a_key()
    {
        var subscription = GivenSubscription(s => s.EntitlementOverrides = """{"glossary":false}""");
        var overrides = new Dictionary<string, JsonElement?>
        {
            [EntitlementConstants.Keys.MaxLanguages] = JsonSerializer.SerializeToElement(8),
            [EntitlementConstants.Keys.VoiceClone] = JsonSerializer.SerializeToElement(true),
            [EntitlementConstants.Keys.Glossary] = null,
        };

        var result = await _service.SetEntitlementOverridesAsync(
            _workspaceId, new AdminEntitlementOverridesRequest(overrides, "Enterprise pilot contract"), _actor);

        result.IsSuccess.Should().BeTrue(result.Error);
        AdminWorkspaceBillingService.ReadContractOverrides(subscription.EntitlementOverrides)
            .Should().BeEquivalentTo(new Dictionary<string, object> { ["max_languages"] = 8L, ["voice_clone"] = true });
        _calls.Should().Equal("audit:succeeded", "save");
    }

    [Theory]
    [InlineData("unknown_key", "1")]
    [InlineData("max_languages", "true")]
    [InlineData("max_languages", "-1")]
    [InlineData("voice_clone", "3")]
    public async Task Entitlement_overrides_refuse_an_unknown_key_or_a_value_of_the_wrong_shape(string key, string json)
    {
        GivenSubscription();
        var overrides = new Dictionary<string, JsonElement?> { [key] = JsonDocument.Parse(json).RootElement.Clone() };

        var result = await _service.SetEntitlementOverridesAsync(
            _workspaceId, new AdminEntitlementOverridesRequest(overrides, "reason"), _actor);

        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
        _calls.Should().BeEmpty();
    }

    // ── mark invoice paid ───────────────────────────────────────────────────────────────────

    private Invoice GivenInvoice(Guid workspaceId, string status = InvoiceConstants.InvoiceStatuses.Open)
    {
        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            InvoiceNumber = "INV-2026-0042",
            Status = status,
            Total = 1_100_000m,
            Currency = "VND",
            Payment = new Payment
            {
                Id = Guid.NewGuid(),
                Status = PaymentConstants.PaymentStatuses.Pending,
                Currency = "VND",
                Subscription = new Subscription { Id = Guid.NewGuid(), WorkspaceId = workspaceId },
            },
        };
        _invoices
            .Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Invoice, bool>>>(), "Payment.Subscription", It.IsAny<CancellationToken>()))
            .ReturnsAsync(invoice);
        return invoice;
    }

    [Fact]
    public async Task Mark_paid_settles_the_invoice_and_its_payment_after_recording_it()
    {
        var invoice = GivenInvoice(_workspaceId);

        var result = await _service.MarkInvoicePaidAsync(
            _workspaceId, invoice.Id, new AdminMarkInvoicePaidRequest("Bank transfer ref VCB-8812"), _actor);

        result.IsSuccess.Should().BeTrue(result.Error);
        invoice.Status.Should().Be(InvoiceConstants.InvoiceStatuses.Paid);
        invoice.Payment.Status.Should().Be(PaymentConstants.PaymentStatuses.Paid);
        _calls.Should().Equal("audit:succeeded", "save");
    }

    [Fact]
    public async Task Mark_paid_will_not_settle_another_workspaces_invoice()
    {
        var invoice = GivenInvoice(Guid.NewGuid());

        var result = await _service.MarkInvoicePaidAsync(
            _workspaceId, invoice.Id, new AdminMarkInvoicePaidRequest("ref"), _actor);

        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
        invoice.Status.Should().Be(InvoiceConstants.InvoiceStatuses.Open);
        _calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Mark_paid_needs_a_reason()
    {
        var invoice = GivenInvoice(_workspaceId);

        var result = await _service.MarkInvoicePaidAsync(
            _workspaceId, invoice.Id, new AdminMarkInvoicePaidRequest(""), _actor);

        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
        _calls.Should().BeEmpty();
    }

    // ── the burn chart ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Burn_is_zero_filled_and_takes_the_balance_from_the_days_last_ledger_row()
    {
        var from = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var ledger = new List<LedgerPoint>
        {
            new(from.AddHours(9), "consume", -40, 960),
            new(from.AddHours(15), "consume", -60, 900),
            new(from.AddDays(2).AddHours(1), "adjustment", 500, 1400),
        };

        var burn = AdminWorkspaceBillingService.Burn(ledger, from, from.AddDays(3));

        burn.Select(p => p.Date).Should().Equal("2026-09-01", "2026-09-02", "2026-09-03");
        burn[0].Should().Be(new AdminWorkspaceBurnPointDto("2026-09-01", 100, 0, 900));
        burn[1].Should().Be(new AdminWorkspaceBurnPointDto("2026-09-02", 0, 0, null));
        burn[2].Should().Be(new AdminWorkspaceBurnPointDto("2026-09-03", 0, 500, 1400));
    }

    [Fact]
    public void Every_audit_verb_fits_the_action_column()
    {
        AdminAuditWorkspaceActions.All.Should().OnlyContain(action => action.Length <= AdminAuditWorkspaceActions.MaxLength);
    }

    private sealed class FixedClock(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now);
    }
}
