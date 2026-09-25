using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
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
using WarpTalk.Shared.PlatformSettings;
using Xunit;

namespace WarpTalk.BillingService.Tests.Application.Services;

/// <summary>
/// The owner of an expired workspace sees what it kept ("X credits kept — renew to use them"), and
/// support can correct it through the audited Adjust Credit action. Both work with NO live
/// subscription, which is exactly when the ordinary balance endpoint answers "not found".
/// </summary>
public class FrozenCreditsReadAndAdjustTests
{
    private readonly List<Subscription> _subscriptions = new();
    private readonly List<CreditTransaction> _ledger = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<ISubscriptionRepository> _subscriptionRepository = new();
    private readonly Mock<IPlatformSettings> _settings = new();
    private readonly Guid _workspaceId = Guid.NewGuid();

    public FrozenCreditsReadAndAdjustTests()
    {
        _subscriptionRepository
            .Setup(r => r.FindAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Expression<Func<Subscription, bool>> p, CancellationToken _) => _subscriptions.Where(p.Compile()).ToList());
        _subscriptionRepository
            .Setup(r => r.GetActiveByWorkspaceIdAsync(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, bool _, bool _, CancellationToken _) =>
                _subscriptions.FirstOrDefault(s => s.WorkspaceId == id && s.IsActive));

        var ledger = new Mock<ICreditTransactionRepository>();
        ledger
            .Setup(r => r.AddAsync(It.IsAny<CreditTransaction>(), It.IsAny<CancellationToken>()))
            .Callback<CreditTransaction, CancellationToken>((tx, _) => _ledger.Add(tx))
            .Returns(Task.CompletedTask);

        _unitOfWork.Setup(u => u.SubscriptionRepository).Returns(_subscriptionRepository.Object);
        _unitOfWork.Setup(u => u.CreditTransactionRepository).Returns(ledger.Object);
        // The platform settings console sets the grace window; 45 here proves the live value is used.
        _settings
            .Setup(p => p.GetInt32Async(
                "billing.frozen_credits.grace_days", It.IsAny<int?>(), It.IsAny<SettingContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(45);
    }

    private CreditService Service() => new(
        _unitOfWork.Object,
        NullLogger<CreditService>.Instance,
        Mock.Of<IUsageSettlementService>(),
        Mock.Of<IWorkspaceClient>(),
        _settings.Object);

    private Subscription Ended(int frozen, DateTime periodEnd)
    {
        var subscription = new Subscription
        {
            Id = Guid.NewGuid(),
            WorkspaceId = _workspaceId,
            UserId = Guid.NewGuid(),
            IsActive = false,
            Status = SubscriptionConstants.SubscriptionStatuses.Expired,
            CurrentPeriodEnd = periodEnd,
            CreditsFrozenAt = periodEnd.AddHours(1),
            FrozenCredits = frozen,
        };
        _subscriptions.Add(subscription);
        return subscription;
    }

    [Fact]
    public async Task An_expired_workspace_reads_the_credits_it_kept_and_when_they_go_dormant()
    {
        var ended = new DateTime(2026, 9, 23, 10, 48, 0, DateTimeKind.Utc);
        Ended(frozen: 2_500, ended);

        var result = await Service().GetFrozenCreditsAsync(_workspaceId);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.FrozenCredits.Should().Be(2_500);
        result.Value.HasActiveSubscription.Should().BeFalse();
        result.Value.EndedAt.Should().Be(ended);
        result.Value.GraceEndsAt.Should().Be(ended.AddDays(45));
        result.Value.DormantSince.Should().BeNull();
    }

    [Fact]
    public async Task A_workspace_with_nothing_frozen_reads_zero_not_an_error()
    {
        var result = await Service().GetFrozenCreditsAsync(_workspaceId);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.FrozenCredits.Should().Be(0);
    }

    [Fact]
    public async Task Adjust_credit_on_a_workspace_without_a_live_plan_adjusts_the_frozen_credits_and_is_ledgered()
    {
        var ended = Ended(frozen: 1_000, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));
        var admin = Guid.NewGuid();

        var staged = await Service().StageWorkspaceAdjustmentAsync(_workspaceId, -400, "Refund of an unused pack", admin);

        staged.IsSuccess.Should().BeTrue(staged.Error);
        staged.Value!.FrozenBefore.Should().Be(1_000);
        ended.FrozenCredits.Should().Be(600);
        ended.CreditsRemaining.Should().Be(0, "the spendable balance of an ended row does not move");
        var entry = _ledger.Single();
        entry.Amount.Should().Be(-400);
        entry.UserId.Should().Be(admin);
        entry.ReferenceType.Should().Be(TransactionConstants.ReferenceTypes.FrozenCreditAdjustment);
    }

    [Fact]
    public async Task Frozen_credits_cannot_be_adjusted_below_zero()
    {
        Ended(frozen: 100, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));

        var staged = await Service().StageWorkspaceAdjustmentAsync(_workspaceId, -101, "Too much", Guid.NewGuid());

        staged.IsSuccess.Should().BeFalse();
        _ledger.Should().BeEmpty();
    }
}
