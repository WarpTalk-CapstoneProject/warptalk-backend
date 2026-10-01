using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Application.Services.PaymentEventHandlers;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Models;
using Xunit;

namespace WarpTalk.BillingService.Tests.Application;

/// <summary>
/// WT-878 — paying must lift the suspension it paid for.
///
/// A top-up or credit pack only added credits, so a workspace suspended at its overage cap stayed
/// suspended (and settlement re-suspended it on the next charge: the overage counter still sat at
/// the cap). Paying an overdue invoice by card matched no handler at all. Both waited for a
/// platform admin to press Resume. And billing.credits_updated had no publisher.
/// </summary>
public class SuspensionLiftPaymentHandlerTests
{
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<ISubscriptionRepository> _subscriptions = new();
    private readonly Mock<ICreditTransactionRepository> _creditTransactions = new();
    private readonly Mock<ICreditPackRepository> _packs = new();
    private readonly Mock<ICreditPackPurchaseRepository> _purchases = new();
    private readonly Mock<IInvoiceRepository> _invoiceRepository = new();
    private readonly Mock<IAiServiceStateStore> _aiState = new();
    private readonly Mock<IBillingMessagePublisher> _publisher = new();
    private readonly List<Invoice> _invoices = new();
    private readonly List<RealtimeNotificationMessage> _published = new();
    private readonly SuspensionLiftService _lift;

    private readonly Guid _workspaceId = Guid.NewGuid();
    private readonly Guid _ownerId = Guid.NewGuid();
    private readonly CreditPack _pack = new()
    {
        Id = Guid.NewGuid(), Slug = "boost-5k", Name = "Boost 5K", Credits = 5_000, BonusCredits = 0, ValidityDays = 90,
    };

    public SuspensionLiftPaymentHandlerTests()
    {
        _unitOfWork.Setup(u => u.SubscriptionRepository).Returns(_subscriptions.Object);
        _unitOfWork.Setup(u => u.CreditTransactionRepository).Returns(_creditTransactions.Object);
        _unitOfWork.Setup(u => u.CreditPacks).Returns(_packs.Object);
        _unitOfWork.Setup(u => u.CreditPackPurchases).Returns(_purchases.Object);
        _unitOfWork.Setup(u => u.InvoiceRepository).Returns(_invoiceRepository.Object);
        _packs.Setup(r => r.GetByIdAsync(_pack.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_pack);
        _purchases.Setup(r => r.ExistsForSessionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

        _invoiceRepository
            .Setup(r => r.FindAsync(It.IsAny<Expression<Func<Invoice, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((Expression<Func<Invoice, bool>> predicate, string _, CancellationToken _) =>
                Task.FromResult<IReadOnlyList<Invoice>>(_invoices.Where(predicate.Compile()).ToList()));
        _invoiceRepository
            .Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Invoice, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((Expression<Func<Invoice, bool>> predicate, string _, CancellationToken _) =>
                Task.FromResult(_invoices.FirstOrDefault(predicate.Compile())));

        _aiState
            .Setup(s => s.SetAiServiceStateAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        _publisher
            .Setup(p => p.PublishAsync(It.IsAny<string>(), It.IsAny<RealtimeNotificationMessage>(), It.IsAny<CancellationToken>()))
            .Callback<string, RealtimeNotificationMessage, CancellationToken>((_, m, _) => _published.Add(m))
            .Returns(Task.CompletedTask);

        _lift = new SuspensionLiftService(
            _unitOfWork.Object, NullLogger<SuspensionLiftService>.Instance, _aiState.Object, _publisher.Object);
    }

    private Subscription Subscription(string state, string? reason, int balance, int overage = 0) => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = _workspaceId,
        UserId = _ownerId,
        IsActive = true,
        Status = SubscriptionConstants.SubscriptionStatuses.Active,
        ServiceState = state,
        SuspendedReason = reason,
        CreditsRemaining = balance,
        OverageCreditsThisCycle = overage,
        OverageStartedAt = overage > 0 ? DateTime.UtcNow.AddDays(-2) : null,
        Plan = new Plan { InvoiceGraceHours = 0 },
    };

    private PaymentEventContext Context(string paymentType, int credits, Subscription? subscription, Payment? existingPayment = null, string status = PaymentConstants.PaymentStatuses.Paid) => new(
        new StripePaymentEventRequest(
            StripeSessionId: "cs_test_wt878",
            PaymentIntentId: "pi_test",
            Amount: 40_000m,
            Currency: PaymentConstants.Currencies.Vnd,
            UserIdStr: _ownerId.ToString(),
            WorkspaceIdStr: _workspaceId.ToString(),
            PaymentType: paymentType,
            Status: status,
            Credits: credits,
            PackageId: _pack.Id.ToString()),
        _workspaceId,
        _ownerId,
        "cs_test_wt878",
        status,
        Guid.NewGuid(),
        existingPayment,
        subscription);

    private static async Task CommitAsync(PaymentEventContext context)
    {
        // What PaymentAppService does after its SaveChanges.
        foreach (var action in context.AfterCommit)
        {
            await action(CancellationToken.None);
        }
    }

    // ── top-up / credit pack ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ATopUpLiftsAnOverageCapSuspensionAndSettlesTheCounter()
    {
        var subscription = Subscription(SubscriptionConstants.ServiceStates.Suspended, SubscriptionConstants.SuspendedReasons.OverageCap, balance: -1_000, overage: 1_000);
        var handler = new CreditTopUpPaymentEventHandler(
            _unitOfWork.Object, NullLogger<CreditTopUpPaymentEventHandler>.Instance, null, _lift);
        var context = Context(PaymentConstants.PaymentTypes.CreditTopUp, 10_000, subscription);

        var result = await handler.HandleAsync(context);

        result.IsSuccess.Should().BeTrue(result.Error);
        subscription.CreditsRemaining.Should().Be(9_000);
        subscription.ServiceState.Should().Be(SubscriptionConstants.ServiceStates.Healthy);
        subscription.SuspendedReason.Should().BeNull();
        subscription.OverageCreditsThisCycle.Should().Be(0);
        context.SubscriptionChanged.Should().BeTrue("PaymentAppService pushes the lifted state to AI for exactly this flag");
    }

    [Fact]
    public async Task ATopUpPublishesCreditsUpdatedAfterTheCommitOnly()
    {
        var subscription = Subscription(SubscriptionConstants.ServiceStates.Suspended, SubscriptionConstants.SuspendedReasons.OverageCap, balance: -1_000, overage: 1_000);
        var handler = new CreditTopUpPaymentEventHandler(
            _unitOfWork.Object, NullLogger<CreditTopUpPaymentEventHandler>.Instance, null, _lift);
        var context = Context(PaymentConstants.PaymentTypes.CreditTopUp, 10_000, subscription);

        await handler.HandleAsync(context);
        _published.Should().BeEmpty("nothing is announced before the balance is committed");

        await CommitAsync(context);

        var message = _published.Should().ContainSingle().Subject;
        message.Type.Should().Be(BillingMessageConstants.Notifications.Types.CreditsUpdated);
        message.UserId.Should().Be(_ownerId.ToString());
        message.PayloadJson.Should().Contain("\"new_balance\":9000");
        message.Content.Should().Contain("resumed");
    }

    [Fact]
    public async Task ATopUpThatLeavesTheBalanceNegativeDoesNotLift()
    {
        var subscription = Subscription(SubscriptionConstants.ServiceStates.Suspended, SubscriptionConstants.SuspendedReasons.OverageCap, balance: -1_000, overage: 1_000);
        var handler = new CreditTopUpPaymentEventHandler(
            _unitOfWork.Object, NullLogger<CreditTopUpPaymentEventHandler>.Instance, null, _lift);
        var context = Context(PaymentConstants.PaymentTypes.CreditTopUp, 400, subscription);

        await handler.HandleAsync(context);
        await CommitAsync(context);

        subscription.ServiceState.Should().Be(SubscriptionConstants.ServiceStates.Suspended);
        subscription.SuspendedReason.Should().Be(SubscriptionConstants.SuspendedReasons.OverageCap);
        _published.Should().ContainSingle("the balance still changed").Which.Content.Should().NotContain("resumed");
    }

    [Fact]
    public async Task ATopUpNeverLiftsATrialEndedSuspension()
    {
        var subscription = Subscription(SubscriptionConstants.ServiceStates.Suspended, SubscriptionConstants.SuspendedReasons.TrialEnded, balance: 0);
        var handler = new CreditTopUpPaymentEventHandler(
            _unitOfWork.Object, NullLogger<CreditTopUpPaymentEventHandler>.Instance, null, _lift);

        await handler.HandleAsync(Context(PaymentConstants.PaymentTypes.CreditTopUp, 10_000, subscription));

        subscription.SuspendedReason.Should().Be(SubscriptionConstants.SuspendedReasons.TrialEnded);
    }

    [Fact]
    public async Task ACreditPackLiftsAnOverageCapSuspensionAndPublishesCreditsUpdated()
    {
        var subscription = Subscription(SubscriptionConstants.ServiceStates.Suspended, SubscriptionConstants.SuspendedReasons.OverageCap, balance: -500, overage: 500);
        var handler = new CreditPackPaymentEventHandler(
            _unitOfWork.Object, NullLogger<CreditPackPaymentEventHandler>.Instance, null, _lift);
        var context = Context(PaymentConstants.PaymentTypes.CreditPack, 5_000, subscription);

        var result = await handler.HandleAsync(context);
        await CommitAsync(context);

        result.IsSuccess.Should().BeTrue(result.Error);
        subscription.CreditsRemaining.Should().Be(4_500);
        subscription.ServiceState.Should().Be(SubscriptionConstants.ServiceStates.Healthy);
        subscription.OverageCreditsThisCycle.Should().Be(0);
        _published.Should().ContainSingle().Which.Type.Should().Be(BillingMessageConstants.Notifications.Types.CreditsUpdated);
    }

    // ── invoice payment ────────────────────────────────────────────────────────────────────

    private (Invoice Invoice, Payment Payment) OverdueInvoice(Subscription subscription, int daysOverdue)
    {
        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            Subscription = subscription,
            SubscriptionId = subscription.Id,
            Status = PaymentConstants.PaymentStatuses.Pending,
            ProviderTransactionId = "cs_test_wt878",
        };
        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            PaymentId = payment.Id,
            Payment = payment,
            Status = InvoiceConstants.InvoiceStatuses.Issued,
            InvoiceNumber = "INV-878",
            DueAt = DateTime.UtcNow.AddDays(-daysOverdue),
            LineItems = "[]",
            Currency = "VND",
        };
        _invoices.Add(invoice);
        return (invoice, payment);
    }

    private InvoiceService InvoiceService() => new(
        _unitOfWork.Object,
        NullLogger<InvoiceService>.Instance,
        Mock.Of<IStripePaymentService>(),
        Mock.Of<IWorkspaceClient>(),
        _lift);

    private InvoicePaymentEventHandler InvoiceHandler() => new(
        InvoiceService(), NullLogger<InvoicePaymentEventHandler>.Instance, _lift);

    [Fact]
    public void TheInvoicePaymentTypeIsClaimedSoItIsNoLongerUnhandled()
    {
        var handler = InvoiceHandler();

        handler.CanHandle(Context(PaymentConstants.PaymentTypes.InvoicePayment, 0, null)).Should().BeTrue();
        handler.CanHandle(Context("invoicepayment", 0, null)).Should().BeTrue();
        handler.CanHandle(Context(PaymentConstants.PaymentTypes.CreditTopUp, 0, null)).Should().BeFalse();
    }

    [Fact]
    public async Task PayingTheOverdueInvoiceSettlesItAndLiftsInvoiceOverdue()
    {
        var subscription = Subscription(SubscriptionConstants.ServiceStates.Suspended, SubscriptionConstants.SuspendedReasons.InvoiceOverdue, balance: 50_000);
        var (invoice, payment) = OverdueInvoice(subscription, daysOverdue: 30);
        var context = Context(PaymentConstants.PaymentTypes.InvoicePayment, 0, subscription, payment);

        var result = await InvoiceHandler().HandleAsync(context);

        result.IsSuccess.Should().BeTrue(result.Error);
        invoice.Status.Should().Be(InvoiceConstants.InvoiceStatuses.Paid);
        payment.Status.Should().Be(PaymentConstants.PaymentStatuses.Paid);
        subscription.ServiceState.Should().Be(SubscriptionConstants.ServiceStates.Healthy);
        subscription.SuspendedReason.Should().BeNull();
        context.SubscriptionChanged.Should().BeFalse("an invoice payment is not a subscription start");

        _aiState.VerifyNoOtherCalls();
        await CommitAsync(context);

        _aiState.Verify(s => s.SetAiServiceStateAsync(
            _workspaceId, SubscriptionConstants.ServiceStates.Healthy, null, It.IsAny<CancellationToken>()), Times.Once);
        _published.Should().ContainSingle().Which.Type.Should().Be(BillingMessageConstants.Notifications.Types.CreditsUpdated);
    }

    [Fact]
    public async Task AnotherOverdueInvoiceKeepsTheWorkspaceSuspended()
    {
        var subscription = Subscription(SubscriptionConstants.ServiceStates.Suspended, SubscriptionConstants.SuspendedReasons.InvoiceOverdue, balance: 50_000);
        var (invoice, payment) = OverdueInvoice(subscription, daysOverdue: 60);
        OverdueInvoice(subscription, daysOverdue: 30).Payment.ProviderTransactionId = "cs_other";
        var context = Context(PaymentConstants.PaymentTypes.InvoicePayment, 0, subscription, payment);

        await InvoiceHandler().HandleAsync(context);
        await CommitAsync(context);

        invoice.Status.Should().Be(InvoiceConstants.InvoiceStatuses.Paid, "the invoice paid is still settled");
        subscription.SuspendedReason.Should().Be(SubscriptionConstants.SuspendedReasons.InvoiceOverdue);
        _aiState.VerifyNoOtherCalls();
        _published.Should().BeEmpty();
    }

    [Fact]
    public async Task AReplayedInvoicePaymentIsIdempotent()
    {
        var subscription = Subscription(SubscriptionConstants.ServiceStates.Suspended, SubscriptionConstants.SuspendedReasons.InvoiceOverdue, balance: 50_000);
        var (invoice, payment) = OverdueInvoice(subscription, daysOverdue: 30);
        var handler = InvoiceHandler();

        var first = Context(PaymentConstants.PaymentTypes.InvoicePayment, 0, subscription, payment);
        await handler.HandleAsync(first);
        await CommitAsync(first);
        var paidAt = invoice.PaidAt;

        var replay = Context(PaymentConstants.PaymentTypes.InvoicePayment, 0, subscription, payment);
        var result = await handler.HandleAsync(replay);
        await CommitAsync(replay);

        result.IsSuccess.Should().BeTrue(result.Error);
        invoice.Status.Should().Be(InvoiceConstants.InvoiceStatuses.Paid);
        invoice.PaidAt.Should().Be(paidAt, "a replay does not re-settle");
        subscription.ServiceState.Should().Be(SubscriptionConstants.ServiceStates.Healthy);
        _aiState.Verify(s => s.SetAiServiceStateAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        _published.Should().ContainSingle("the replay lifted nothing, so it announces nothing");
    }

    [Fact]
    public async Task AnInvoicePaymentWithNoMatchingInvoiceStillSucceedsAndLiftsNothing()
    {
        var subscription = Subscription(SubscriptionConstants.ServiceStates.Suspended, SubscriptionConstants.SuspendedReasons.InvoiceOverdue, balance: 50_000);
        var context = Context(PaymentConstants.PaymentTypes.InvoicePayment, 0, subscription, existingPayment: null);

        var result = await InvoiceHandler().HandleAsync(context);

        result.IsSuccess.Should().BeTrue("the money is recorded by the caller either way");
        subscription.SuspendedReason.Should().Be(SubscriptionConstants.SuspendedReasons.InvoiceOverdue);
        context.AfterCommit.Should().BeEmpty();
    }

    [Fact]
    public async Task AnUnpaidInvoicePaymentEventDoesNothing()
    {
        var subscription = Subscription(SubscriptionConstants.ServiceStates.Suspended, SubscriptionConstants.SuspendedReasons.InvoiceOverdue, balance: 50_000);
        var (invoice, payment) = OverdueInvoice(subscription, daysOverdue: 30);

        var result = await InvoiceHandler().HandleAsync(
            Context(PaymentConstants.PaymentTypes.InvoicePayment, 0, subscription, payment, PaymentConstants.PaymentStatuses.Failed));

        result.IsSuccess.Should().BeTrue();
        invoice.Status.Should().Be(InvoiceConstants.InvoiceStatuses.Issued);
        subscription.SuspendedReason.Should().Be(SubscriptionConstants.SuspendedReasons.InvoiceOverdue);
    }

    // ── admin mark paid ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AdminMarkPaidRunsTheSameLiftAndPushesToAi()
    {
        var subscription = Subscription(SubscriptionConstants.ServiceStates.Suspended, SubscriptionConstants.SuspendedReasons.InvoiceOverdue, balance: 50_000);
        var (invoice, _) = OverdueInvoice(subscription, daysOverdue: 30);

        var result = await InvoiceService().MarkInvoicePaidAsync(invoice.Id);

        result.IsSuccess.Should().BeTrue(result.Error);
        invoice.Status.Should().Be(InvoiceConstants.InvoiceStatuses.Paid);
        subscription.SuspendedReason.Should().BeNull();
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        _aiState.Verify(s => s.SetAiServiceStateAsync(
            _workspaceId, SubscriptionConstants.ServiceStates.Healthy, null, It.IsAny<CancellationToken>()), Times.Once);
        _published.Should().ContainSingle().Which.Type.Should().Be(BillingMessageConstants.Notifications.Types.CreditsUpdated);
    }

    [Fact]
    public async Task AdminMarkPaidOnAnAlreadyPaidInvoiceStillFreesAWorkspaceStuckBeforeTheFix()
    {
        var subscription = Subscription(SubscriptionConstants.ServiceStates.Suspended, SubscriptionConstants.SuspendedReasons.InvoiceOverdue, balance: 50_000);
        var (invoice, _) = OverdueInvoice(subscription, daysOverdue: 30);
        invoice.MarkPaid(DateTime.UtcNow.AddDays(-1));

        var result = await InvoiceService().MarkInvoicePaidAsync(invoice.Id);

        result.IsSuccess.Should().BeTrue(result.Error);
        subscription.SuspendedReason.Should().BeNull();
    }

    [Fact]
    public async Task AdminMarkPaidOnAPaidInvoiceOfAHealthyWorkspaceSavesNothing()
    {
        var subscription = Subscription(SubscriptionConstants.ServiceStates.Healthy, null, balance: 50_000);
        var (invoice, _) = OverdueInvoice(subscription, daysOverdue: 30);
        invoice.MarkPaid(DateTime.UtcNow.AddDays(-1));

        await InvoiceService().MarkInvoicePaidAsync(invoice.Id);

        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        _aiState.VerifyNoOtherCalls();
    }
}
