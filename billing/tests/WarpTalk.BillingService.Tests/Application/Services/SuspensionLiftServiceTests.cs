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
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Models;
using Xunit;

namespace WarpTalk.BillingService.Tests.Application.Services;

/// <summary>
/// WT-878 — the one rule for "money arrived: may this workspace resume?". Paying lifts the
/// suspension it paid for (overage_cap on credits, invoice_overdue on a settled invoice) and
/// nothing else.
/// </summary>
public class SuspensionLiftServiceTests
{
    private static readonly DateTime Now = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);

    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IInvoiceRepository> _invoiceRepository = new();
    private readonly Mock<IAiServiceStateStore> _aiState = new();
    private readonly Mock<IBillingMessagePublisher> _publisher = new();
    private readonly List<Invoice> _invoices = new();
    private readonly SuspensionLiftService _service;
    private readonly Guid _workspaceId = Guid.NewGuid();

    public SuspensionLiftServiceTests()
    {
        _unitOfWork.Setup(u => u.InvoiceRepository).Returns(_invoiceRepository.Object);
        _invoiceRepository
            .Setup(r => r.FindAsync(It.IsAny<Expression<Func<Invoice, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((Expression<Func<Invoice, bool>> predicate, string _, CancellationToken _) =>
                Task.FromResult<IReadOnlyList<Invoice>>(_invoices.Where(predicate.Compile()).ToList()));
        _aiState
            .Setup(s => s.SetAiServiceStateAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        _service = new SuspensionLiftService(
            _unitOfWork.Object, NullLogger<SuspensionLiftService>.Instance, _aiState.Object, _publisher.Object);
    }

    private Subscription Suspended(string? reason, int balance, int overage = 0) => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = _workspaceId,
        UserId = Guid.NewGuid(),
        IsActive = true,
        Status = SubscriptionConstants.SubscriptionStatuses.Active,
        ServiceState = SubscriptionConstants.ServiceStates.Suspended,
        SuspendedReason = reason,
        CreditsRemaining = balance,
        OverageCreditsThisCycle = overage,
        OverageStartedAt = overage > 0 ? Now.AddDays(-3) : null,
    };

    private Invoice OpenInvoice(Subscription subscription, DateTime? dueAt, int graceHours = 0, string status = InvoiceConstants.InvoiceStatuses.Issued)
    {
        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            Status = status,
            DueAt = dueAt,
            Payment = new Payment { Id = Guid.NewGuid(), Subscription = subscription },
        };
        subscription.Plan ??= new Plan { InvoiceGraceHours = graceHours };
        _invoices.Add(invoice);
        return invoice;
    }

    // ── overage_cap ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ACreditGrantThatMakesTheBalancePositiveLiftsOverageCap()
    {
        // Suspended at exactly the cap: 1,000 overage credits, balance -1,000; then a 5,000 top-up.
        var subscription = Suspended(SubscriptionConstants.SuspendedReasons.OverageCap, balance: -1_000 + 5_000, overage: 1_000);

        var outcome = _service.StageAfterCreditGrant(subscription, Now);

        outcome.Lifted.Should().BeTrue();
        outcome.LiftedReason.Should().Be(SubscriptionConstants.SuspendedReasons.OverageCap);
        subscription.ServiceState.Should().Be(SubscriptionConstants.ServiceStates.Healthy);
        subscription.SuspendedReason.Should().BeNull();
    }

    [Fact]
    public void TheOverageThatTheGrantPaidOffIsSettledSoTheNextChargeDoesNotReSuspend()
    {
        // settle_usage_charge: new_overage = counter + delta, and delta is 0 while the balance covers
        // the charge. Left at the cap, the very next charge suspends again ("= cap AND cap > 0").
        var subscription = Suspended(SubscriptionConstants.SuspendedReasons.OverageCap, balance: 4_000, overage: 1_000);

        var outcome = _service.StageAfterCreditGrant(subscription, Now);

        outcome.OverageSettled.Should().Be(1_000);
        subscription.OverageCreditsThisCycle.Should().Be(0, "a positive balance means no overage is still owed");
        subscription.OverageStartedAt.Should().BeNull("settlement keeps overage_started_at NULL whenever the counter is 0");
    }

    [Fact]
    public void AGrantThatLeavesTheBalanceNegativeKeepsTheSuspensionAndOnlySettlesWhatItCovered()
    {
        // -1,000 owed, 400 bought: still 600 below zero.
        var subscription = Suspended(SubscriptionConstants.SuspendedReasons.OverageCap, balance: -600, overage: 1_000);

        var outcome = _service.StageAfterCreditGrant(subscription, Now);

        outcome.Lifted.Should().BeFalse();
        subscription.ServiceState.Should().Be(SubscriptionConstants.ServiceStates.Suspended);
        subscription.SuspendedReason.Should().Be(SubscriptionConstants.SuspendedReasons.OverageCap);
        subscription.OverageCreditsThisCycle.Should().Be(600, "the counter is capped at the debt still owed");
        subscription.OverageStartedAt.Should().NotBeNull();
    }

    [Fact]
    public void AZeroBalanceIsNotPositiveAndDoesNotLift()
    {
        var subscription = Suspended(SubscriptionConstants.SuspendedReasons.OverageCap, balance: 0, overage: 500);

        _service.StageAfterCreditGrant(subscription, Now).Lifted.Should().BeFalse();
        subscription.SuspendedReason.Should().Be(SubscriptionConstants.SuspendedReasons.OverageCap);
    }

    [Fact]
    public void AGrantOnAHealthyWorkspaceChangesNoState()
    {
        var subscription = Suspended(null, balance: 10_000);
        subscription.ServiceState = SubscriptionConstants.ServiceStates.Healthy;

        var outcome = _service.StageAfterCreditGrant(subscription, Now);

        outcome.Should().Be(SuspensionLiftOutcome.None);
        subscription.ServiceState.Should().Be(SubscriptionConstants.ServiceStates.Healthy);
    }

    [Theory]
    [InlineData(SubscriptionConstants.SuspendedReasons.TrialEnded)]
    [InlineData(SubscriptionConstants.SuspendedReasons.SubscriptionExpired)]
    [InlineData(SubscriptionConstants.SuspendedReasons.InvoiceOverdue)]
    [InlineData("admin")]
    [InlineData(null)]
    public void ACreditGrantNeverLiftsAnyOtherSuspension(string? reason)
    {
        var subscription = Suspended(reason, balance: 50_000);

        _service.StageAfterCreditGrant(subscription, Now).Lifted.Should().BeFalse();
        subscription.ServiceState.Should().Be(SubscriptionConstants.ServiceStates.Suspended);
        subscription.SuspendedReason.Should().Be(reason);
    }

    // ── invoice_overdue ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PayingTheOnlyOverdueInvoiceLiftsInvoiceOverdue()
    {
        var subscription = Suspended(SubscriptionConstants.SuspendedReasons.InvoiceOverdue, balance: 20_000);
        var paid = OpenInvoice(subscription, Now.AddDays(-20));

        var outcome = await _service.StageAfterInvoicePaidAsync(subscription, paid.Id, Now);

        outcome.Lifted.Should().BeTrue();
        outcome.LiftedReason.Should().Be(SubscriptionConstants.SuspendedReasons.InvoiceOverdue);
        subscription.ServiceState.Should().Be(SubscriptionConstants.ServiceStates.Healthy);
        subscription.SuspendedReason.Should().BeNull();
    }

    [Fact]
    public async Task AnotherOverdueInvoiceKeepsTheSuspension()
    {
        var subscription = Suspended(SubscriptionConstants.SuspendedReasons.InvoiceOverdue, balance: 20_000);
        var paid = OpenInvoice(subscription, Now.AddDays(-40));
        OpenInvoice(subscription, Now.AddDays(-20));

        var outcome = await _service.StageAfterInvoicePaidAsync(subscription, paid.Id, Now);

        outcome.Lifted.Should().BeFalse();
        subscription.SuspendedReason.Should().Be(SubscriptionConstants.SuspendedReasons.InvoiceOverdue);
    }

    [Fact]
    public async Task AnotherInvoiceStillInsideItsGraceWindowDoesNotHoldTheSuspension()
    {
        // The sweeper would not suspend for it yet, so it must not keep a paid workspace suspended.
        var subscription = Suspended(SubscriptionConstants.SuspendedReasons.InvoiceOverdue, balance: 20_000);
        subscription.Plan = new Plan { InvoiceGraceHours = 360 };
        var paid = OpenInvoice(subscription, Now.AddDays(-40));
        OpenInvoice(subscription, Now.AddDays(-2));

        (await _service.StageAfterInvoicePaidAsync(subscription, paid.Id, Now)).Lifted.Should().BeTrue();
    }

    [Theory]
    [InlineData(InvoiceConstants.InvoiceStatuses.Paid)]
    [InlineData(InvoiceConstants.InvoiceStatuses.Void)]
    public async Task SettledOrVoidedInvoicesAreNotOverdue(string status)
    {
        var subscription = Suspended(SubscriptionConstants.SuspendedReasons.InvoiceOverdue, balance: 20_000);
        var paid = OpenInvoice(subscription, Now.AddDays(-40));
        OpenInvoice(subscription, Now.AddDays(-30), status: status);

        (await _service.StageAfterInvoicePaidAsync(subscription, paid.Id, Now)).Lifted.Should().BeTrue();
    }

    [Fact]
    public async Task AnotherWorkspacesOverdueInvoiceIsNotThisWorkspacesProblem()
    {
        var subscription = Suspended(SubscriptionConstants.SuspendedReasons.InvoiceOverdue, balance: 20_000);
        var paid = OpenInvoice(subscription, Now.AddDays(-40));
        var stranger = Suspended(SubscriptionConstants.SuspendedReasons.InvoiceOverdue, balance: 0);
        stranger.WorkspaceId = Guid.NewGuid();
        OpenInvoice(stranger, Now.AddDays(-30));

        (await _service.StageAfterInvoicePaidAsync(subscription, paid.Id, Now)).Lifted.Should().BeTrue();
    }

    [Fact]
    public async Task AnInvoiceOverdueLiftWithANegativeBalanceResumesInOverage()
    {
        var subscription = Suspended(SubscriptionConstants.SuspendedReasons.InvoiceOverdue, balance: -300, overage: 300);
        var paid = OpenInvoice(subscription, Now.AddDays(-20));

        (await _service.StageAfterInvoicePaidAsync(subscription, paid.Id, Now)).Lifted.Should().BeTrue();
        subscription.ServiceState.Should().Be(SubscriptionConstants.ServiceStates.InOverage);
    }

    [Theory]
    [InlineData(SubscriptionConstants.SuspendedReasons.OverageCap)]
    [InlineData(SubscriptionConstants.SuspendedReasons.TrialEnded)]
    [InlineData(SubscriptionConstants.SuspendedReasons.SubscriptionExpired)]
    [InlineData("admin")]
    [InlineData(null)]
    public async Task PayingAnInvoiceNeverLiftsAnyOtherSuspension(string? reason)
    {
        var subscription = Suspended(reason, balance: -500, overage: 500);
        var paid = OpenInvoice(subscription, Now.AddDays(-20));

        (await _service.StageAfterInvoicePaidAsync(subscription, paid.Id, Now)).Lifted.Should().BeFalse();
        subscription.ServiceState.Should().Be(SubscriptionConstants.ServiceStates.Suspended);
        subscription.SuspendedReason.Should().Be(reason);
    }

    // ── after commit ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheServiceStateIsPushedToAiForTheLiveSubscription()
    {
        var subscription = Suspended(null, balance: 1);
        subscription.ServiceState = SubscriptionConstants.ServiceStates.Healthy;

        await _service.PushServiceStateAsync(subscription);

        _aiState.Verify(s => s.SetAiServiceStateAsync(
            _workspaceId, SubscriptionConstants.ServiceStates.Healthy, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AnEndedSubscriptionIsNeverPushed()
    {
        var subscription = Suspended(null, balance: 1);
        subscription.IsActive = false;

        await _service.PushServiceStateAsync(subscription);

        _aiState.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AFailedPushDoesNotThrow()
    {
        _aiState
            .Setup(s => s.SetAiServiceStateAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("redis down"));

        var act = () => _service.PushServiceStateAsync(Suspended(null, balance: 1));

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task CreditsUpdatedIsPublishedToTheOwnerWithTheNewBalanceAndState()
    {
        RealtimeNotificationMessage? sent = null;
        string? channel = null;
        _publisher
            .Setup(p => p.PublishAsync(It.IsAny<string>(), It.IsAny<RealtimeNotificationMessage>(), It.IsAny<CancellationToken>()))
            .Callback<string, RealtimeNotificationMessage, CancellationToken>((c, m, _) => { channel = c; sent = m; })
            .Returns(Task.CompletedTask);
        var subscription = Suspended(null, balance: 12_345);
        subscription.ServiceState = SubscriptionConstants.ServiceStates.Healthy;

        await _service.PublishCreditsUpdatedAsync(subscription, resumed: true);

        channel.Should().Be(BillingMessageConstants.Notifications.Channel);
        sent.Should().NotBeNull();
        sent!.Type.Should().Be(BillingMessageConstants.Notifications.Types.CreditsUpdated);
        sent.UserId.Should().Be(subscription.UserId.ToString());
        sent.Content.Should().Contain("resumed");

        using var payload = JsonDocument.Parse(sent.PayloadJson);
        payload.RootElement.GetProperty("new_balance").GetInt32().Should().Be(12_345);
        payload.RootElement.GetProperty("workspace_id").GetString().Should().Be(_workspaceId.ToString());
        payload.RootElement.GetProperty("service_state").GetString().Should().Be(SubscriptionConstants.ServiceStates.Healthy);
    }

    [Fact]
    public async Task AFailedPublishDoesNotThrow()
    {
        _publisher
            .Setup(p => p.PublishAsync(It.IsAny<string>(), It.IsAny<RealtimeNotificationMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("redis down"));

        var act = () => _service.PublishCreditsUpdatedAsync(Suspended(null, balance: 1), resumed: false);

        await act.Should().NotThrowAsync();
    }
}
