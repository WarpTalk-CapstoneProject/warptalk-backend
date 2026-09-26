using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Application.Services.PaymentEventHandlers;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.BillingService.Tests.Application.PackageCatalog;

/// <summary>
/// backend#467 safety net, pack half. Checkout refuses a credit pack without a live subscription;
/// a pack that is paid anyway (a session opened before the plan expired) must be booked FROZEN on
/// the workspace's latest subscription, with its purchase row pointing there so the pack still
/// expires on its own date — never dropped, and never granted as spendable either.
/// </summary>
public class CreditPackFrozenPurchaseTests
{
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<ISubscriptionRepository> _subscriptions = new();
    private readonly Mock<ICreditPackRepository> _packs = new();
    private readonly Mock<ICreditPackPurchaseRepository> _purchases = new();
    private readonly Mock<ICreditFreezeService> _freezer = new();
    private readonly List<CreditPackPurchase> _added = new();

    private readonly Guid _workspaceId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly CreditPack _pack = new()
    {
        Id = Guid.NewGuid(), Slug = "boost-5k", Name = "Boost 5K", Credits = 5_000, BonusCredits = 500, ValidityDays = 90,
    };

    public CreditPackFrozenPurchaseTests()
    {
        _unitOfWork.Setup(u => u.SubscriptionRepository).Returns(_subscriptions.Object);
        _unitOfWork.Setup(u => u.CreditPacks).Returns(_packs.Object);
        _unitOfWork.Setup(u => u.CreditPackPurchases).Returns(_purchases.Object);
        _packs.Setup(r => r.GetByIdAsync(_pack.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_pack);
        _purchases.Setup(r => r.ExistsForSessionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _purchases
            .Setup(r => r.AddAsync(It.IsAny<CreditPackPurchase>(), It.IsAny<CancellationToken>()))
            .Callback<CreditPackPurchase, CancellationToken>((p, _) => _added.Add(p))
            .Returns(Task.CompletedTask);
        _subscriptions
            .Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Subscription?)null);
    }

    private PaymentEventContext PaidPackContext() => new(
        new StripePaymentEventRequest(
            StripeSessionId: "cs_test_467_pack",
            PaymentIntentId: "pi_test",
            Amount: 10m,
            Currency: PaymentConstants.Currencies.Usd,
            UserIdStr: _userId.ToString(),
            WorkspaceIdStr: _workspaceId.ToString(),
            PaymentType: PaymentConstants.PaymentTypes.CreditPack,
            Status: PaymentConstants.PaymentStatuses.Paid,
            Credits: 5_500,
            PackageId: _pack.Id.ToString()),
        _workspaceId,
        _userId,
        "cs_test_467_pack",
        PaymentConstants.PaymentStatuses.Paid,
        Guid.NewGuid(),
        null,
        null);

    [Fact]
    public async Task A_paid_pack_with_no_live_subscription_is_booked_frozen_and_its_purchase_points_at_the_holder()
    {
        var ended = new Subscription
        {
            Id = Guid.NewGuid(), WorkspaceId = _workspaceId, UserId = _userId, IsActive = false,
            Status = SubscriptionConstants.SubscriptionStatuses.Expired, CreditsRemaining = 0,
        };
        _freezer
            .Setup(f => f.StageFrozenPurchaseAsync(It.IsAny<FrozenPurchase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ended);
        var handler = new CreditPackPaymentEventHandler(
            _unitOfWork.Object, NullLogger<CreditPackPaymentEventHandler>.Instance, _freezer.Object);
        var context = PaidPackContext();

        var result = await handler.HandleAsync(context);

        result.IsSuccess.Should().BeTrue(result.Error);
        ended.CreditsRemaining.Should().Be(0, "a frozen booking must not become spendable on an ended plan");
        context.Subscription.Should().BeSameAs(ended);
        context.SubscriptionChanged.Should().BeFalse("nothing about the ended plan changed");
        _freezer.Verify(f => f.StageFrozenPurchaseAsync(
            It.Is<FrozenPurchase>(p => p.Credits == 5_500 && p.WorkspaceId == _workspaceId),
            It.IsAny<CancellationToken>()), Times.Once);

        var purchase = _added.Should().ContainSingle().Subject;
        purchase.SubscriptionId.Should().Be(ended.Id, "the pack's own expiry runs from the row holding it");
        purchase.StripeSessionId.Should().Be("cs_test_467_pack", "the session key keeps the return page from granting twice");
        purchase.ExpiresAt.Should().NotBeNull();
    }

    [Fact]
    public async Task A_paid_pack_for_a_workspace_that_never_subscribed_fails_loudly_so_Stripe_retries()
    {
        _freezer
            .Setup(f => f.StageFrozenPurchaseAsync(It.IsAny<FrozenPurchase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Subscription?)null);
        var handler = new CreditPackPaymentEventHandler(
            _unitOfWork.Object, NullLogger<CreditPackPaymentEventHandler>.Instance, _freezer.Object);

        var result = await handler.HandleAsync(PaidPackContext());

        result.IsSuccess.Should().BeFalse();
        _added.Should().BeEmpty();
    }
}
