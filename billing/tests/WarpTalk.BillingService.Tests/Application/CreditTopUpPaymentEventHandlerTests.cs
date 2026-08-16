using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Services.PaymentEventHandlers;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.BillingService.Tests.Application;

/// <summary>
/// WT-429. The handler that never existed.
///
/// "CreditTopUp" was the payment type the web posted, no registered handler claimed it, and
/// PaymentAppService's `if (handler is not null)` had no else — so the request wrote a payment
/// row, issued an invoice, returned success, and granted nothing. The button was switched off
/// (#190) rather than repaired. These tests pin the repair, including the two ways it could
/// silently fail the same way again.
/// </summary>
public class CreditTopUpPaymentEventHandlerTests
{
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<ISubscriptionRepository> _subscriptions = new();
    private readonly Mock<ICreditTransactionRepository> _creditTransactions = new();
    private readonly List<CreditTransaction> _ledger = new();
    private readonly CreditTopUpPaymentEventHandler _handler;

    private readonly Guid _workspaceId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();

    public CreditTopUpPaymentEventHandlerTests()
    {
        _unitOfWork.Setup(u => u.SubscriptionRepository).Returns(_subscriptions.Object);
        _unitOfWork.Setup(u => u.CreditTransactionRepository).Returns(_creditTransactions.Object);
        _creditTransactions
            .Setup(r => r.AddAsync(It.IsAny<CreditTransaction>(), It.IsAny<CancellationToken>()))
            .Callback<CreditTransaction, CancellationToken>((tx, _) => _ledger.Add(tx))
            .Returns(Task.CompletedTask);

        _handler = new CreditTopUpPaymentEventHandler(
            _unitOfWork.Object,
            Mock.Of<ILogger<CreditTopUpPaymentEventHandler>>());
    }

    private PaymentEventContext Context(
        int credits,
        string status = PaymentConstants.PaymentStatuses.Paid,
        Subscription? subscription = null,
        string paymentType = PaymentConstants.PaymentTypes.CreditTopUp)
    {
        var request = new StripePaymentEventRequest(
            StripeSessionId: "cs_test_wt429",
            PaymentIntentId: "pi_test",
            Amount: 40_000m,
            Currency: PaymentConstants.Currencies.Vnd,
            UserIdStr: _userId.ToString(),
            WorkspaceIdStr: _workspaceId.ToString(),
            PaymentType: paymentType,
            Status: status,
            Credits: credits);

        return new PaymentEventContext(
            request,
            _workspaceId,
            _userId,
            providerTransactionId: "cs_test_wt429",
            parsedPaymentStatus: status,
            paymentId: Guid.NewGuid(),
            existingPayment: null,
            subscription: subscription);
    }

    private Subscription ActiveSubscription(int startingCredits) => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = _workspaceId,
        UserId = _userId,
        IsActive = true,
        Status = SubscriptionConstants.SubscriptionStatuses.Active,
        CreditsRemaining = startingCredits,
    };

    [Fact]
    public void ItClaimsTheTypeTheWebHasBeenPostingAllAlong()
    {
        _handler.CanHandle(Context(10_000)).Should().BeTrue();
        _handler.CanHandle(Context(10_000, paymentType: "creditTOPUP")).Should().BeTrue("payment types are matched case-insensitively elsewhere too");
        _handler.CanHandle(Context(10_000, paymentType: PaymentConstants.PaymentTypes.Subscription)).Should().BeFalse();
    }

    [Fact]
    public async Task APaidTopUpRaisesTheBalanceAndWritesTheLedgerRow()
    {
        var subscription = ActiveSubscription(1_000);

        var result = await _handler.HandleAsync(Context(10_000, subscription: subscription));

        result.IsSuccess.Should().BeTrue(result.Error);
        subscription.CreditsRemaining.Should().Be(11_000);

        var row = _ledger.Should().ContainSingle().Subject;
        row.Amount.Should().Be(10_000);
        row.Type.Should().Be(TransactionConstants.TransactionTypes.TopUp);
        row.BalanceAfter.Should().Be(11_000, "the ledger row must record the balance it produced");
        row.ReferenceType.Should().Be(TransactionConstants.ReferenceTypes.StripePayment);
    }

    [Fact]
    public async Task TheEntitlementRefreshIsRequested()
    {
        // Without SubscriptionChanged the caller does not publish, and consumers keep enforcing
        // the pre-top-up balance until the hourly reconcile — a paid-for balance that does not
        // arrive is the same complaint this ticket is about.
        var subscription = ActiveSubscription(0);
        var context = Context(5_000, subscription: subscription);

        await _handler.HandleAsync(context);

        context.SubscriptionChanged.Should().BeTrue();
        context.Subscription.Should().BeSameAs(subscription);
    }

    [Fact]
    public async Task AnUnpaidEventGrantsNothing()
    {
        var subscription = ActiveSubscription(1_000);

        var result = await _handler.HandleAsync(
            Context(10_000, status: PaymentConstants.PaymentStatuses.Failed, subscription: subscription));

        result.IsSuccess.Should().BeTrue("a failed payment is not an error, it is simply nothing to grant");
        subscription.CreditsRemaining.Should().Be(1_000);
        _ledger.Should().BeEmpty();
    }

    [Fact]
    public async Task APaidTopUpCarryingNoCreditCountFailsLoudly()
    {
        // The old bug in a new place: money taken, nothing to grant. Completing quietly here is
        // exactly what made the original incident invisible.
        var subscription = ActiveSubscription(1_000);

        var result = await _handler.HandleAsync(Context(0, subscription: subscription));

        result.IsSuccess.Should().BeFalse();
        subscription.CreditsRemaining.Should().Be(1_000);
        _ledger.Should().BeEmpty();
    }

    [Fact]
    public async Task APaidTopUpWithNoSubscriptionToCreditFailsLoudly()
    {
        _subscriptions
            .Setup(r => r.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<Subscription, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Subscription?)null);

        var result = await _handler.HandleAsync(Context(10_000));

        result.IsSuccess.Should().BeFalse();
        _ledger.Should().BeEmpty();
    }

    [Fact]
    public async Task TheSubscriptionIsLookedUpWhenTheContextDoesNotCarryOne()
    {
        // The webhook path builds a context with no subscription attached; the return path may.
        // Both must land the credits in the same place.
        var subscription = ActiveSubscription(250);
        _subscriptions
            .Setup(r => r.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<Subscription, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(subscription);

        var result = await _handler.HandleAsync(Context(1_500));

        result.IsSuccess.Should().BeTrue(result.Error);
        subscription.CreditsRemaining.Should().Be(1_750);
    }
}
