using WarpTalk.BillingService.Application.Entitlements;
using System.Linq.Expressions;
using Microsoft.Extensions.Logging;
using Moq;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Application.Services.PaymentEventHandlers;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;
using Xunit;

namespace WarpTalk.BillingService.Tests.Application.Services;

public class PaymentAppServiceTests
{
    private readonly Mock<IStripePaymentService> _stripePaymentService = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IPaymentRepository> _paymentRepository = new();
    private readonly Mock<ISubscriptionRepository> _subscriptionRepository = new();
    private readonly Mock<IInvoiceRepository> _invoiceRepository = new();

    private readonly Mock<IBillingMessagePublisher> _messagePublisher = new();

    public PaymentAppServiceTests()
    {
        _unitOfWork.Setup(u => u.PaymentRepository).Returns(_paymentRepository.Object);
        _unitOfWork.Setup(u => u.SubscriptionRepository).Returns(_subscriptionRepository.Object);
        _unitOfWork.Setup(u => u.InvoiceRepository).Returns(_invoiceRepository.Object);
        _unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        _paymentRepository
            .Setup(r => r.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<Payment, bool>>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Payment?)null);
    }


    // ── WT-699 / TC3906: checkout.session.expired ─────────────────────────────────────────

    private void PaymentForSession(Payment? payment) =>
        _paymentRepository
            .Setup(r => r.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<Payment, bool>>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns((Expression<Func<Payment, bool>> predicate, string _, CancellationToken _) =>
                Task.FromResult(payment is not null && predicate.Compile()(payment) ? payment : null));

    [Fact]
    public async Task ExpireCheckoutSessionAsync_MarksThePendingPaymentExpired()
    {
        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            ProviderTransactionId = "cs_test_abandoned",
            Status = PaymentConstants.PaymentStatuses.Pending,
        };
        PaymentForSession(payment);

        var result = await CreateService().ExpireCheckoutSessionAsync("cs_test_abandoned");

        Assert.True(result.IsSuccess);
        Assert.Equal(PaymentConstants.PaymentStatuses.Expired, payment.Status);
        Assert.False(string.IsNullOrWhiteSpace(payment.FailureReason));
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>Idempotent, and never downgrades a payment that completed.</summary>
    [Theory]
    [InlineData(PaymentConstants.PaymentStatuses.Paid)]
    [InlineData(PaymentConstants.PaymentStatuses.Expired)]
    [InlineData(PaymentConstants.PaymentStatuses.Failed)]
    public async Task ExpireCheckoutSessionAsync_LeavesANonPendingPaymentAlone(string status)
    {
        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            ProviderTransactionId = "cs_test_done",
            Status = status,
        };
        PaymentForSession(payment);

        var result = await CreateService().ExpireCheckoutSessionAsync("cs_test_done");

        Assert.True(result.IsSuccess);
        Assert.Equal(status, payment.Status);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExpireCheckoutSessionAsync_AcknowledgesASessionNothingWasRecordedFor()
    {
        PaymentForSession(null);

        var result = await CreateService().ExpireCheckoutSessionAsync("cs_test_plan_checkout");

        Assert.True(result.IsSuccess);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        _paymentRepository.Verify(r => r.AddAsync(It.IsAny<Payment>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessPaymentEventAsync_UnknownPaidPaymentType_PersistsPaymentAndInvoice()
    {
        Payment? addedPayment = null;
        Invoice? addedInvoice = null;

        _subscriptionRepository
            .Setup(r => r.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<Subscription, bool>>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Subscription?)null);

        _paymentRepository
            .Setup(r => r.AddAsync(It.IsAny<Payment>(), It.IsAny<CancellationToken>()))
            .Callback<Payment, CancellationToken>((payment, _) => addedPayment = payment)
            .Returns(Task.CompletedTask);
        _invoiceRepository
            .Setup(r => r.AddAsync(It.IsAny<Invoice>(), It.IsAny<CancellationToken>()))
            .Callback<Invoice, CancellationToken>((invoice, _) => addedInvoice = invoice)
            .Returns(Task.CompletedTask);

        var service = CreateService();

        var result = await service.ProcessPaymentEventAsync(CreateEvent("UnknownPaymentType"));

        Assert.True(result.IsSuccess);
        Assert.NotNull(addedPayment);
        Assert.NotNull(addedInvoice);
        Assert.Equal("cs_test_payment", addedPayment.ProviderTransactionId);
        Assert.Equal(addedPayment.Id, addedInvoice.PaymentId);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// WT-370. This is the path a person lands on the moment Stripe redirects them back, and the
    /// only thing that activates their plan when the webhook does not. It must not depend on
    /// workspace-service answering.
    ///
    /// It used to call VerifyWorkspaceRolesAsync on every visit, and a call that fails for ANY
    /// reason — a restart, a gRPC hiccup, a slow deploy — is indistinguishable here from "you are
    /// not allowed": 403, and the paid-for plan is never applied. Stripe's own session metadata
    /// already names the buyer, so for the buyer there is nothing to ask anyone.
    /// </summary>
    [Fact]
    public async Task GetAndProcessCheckoutSessionAsync_ActivatesForTheBuyer_WithoutAskingWorkspaceService()
    {
        var buyerId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();

        _stripePaymentService
            .Setup(s => s.GetCheckoutSessionAsync("cs_test_buyer", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new CheckoutSessionDto(
                "cs_test_buyer",
                1_900_000,
                PaymentConstants.Currencies.Vnd,
                new Dictionary<string, string>
                {
                    [PaymentConstants.StripeMetadata.UserId] = buyerId.ToString(),
                    [PaymentConstants.StripeMetadata.WorkspaceId] = workspaceId.ToString(),
                    [PaymentConstants.StripeMetadata.PaymentType] = PaymentConstants.PaymentTypes.Subscription,
                    [PaymentConstants.StripeMetadata.PlanSlug] = "enterprise",
                    [PaymentConstants.StripeMetadata.BillingCycle] = PaymentConstants.BillingCycles.Yearly,
                },
                PaymentConstants.Payments.StatusPaid,
                "complete",
                // Subscription-mode sessions carry an invoice, not a payment intent — exactly
                // what the WT-370 payload showed ("payment_intent": null).
                string.Empty)));

        _subscriptionRepository
            .Setup(r => r.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<Subscription, bool>>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Subscription?)null);

        var workspaceClient = new Mock<IWorkspaceClient>();

        var service = new PaymentAppService(
            _stripePaymentService.Object,
            _unitOfWork.Object,
            Mock.Of<ILogger<PaymentAppService>>(),
            _messagePublisher.Object,
            Array.Empty<IPaymentEventHandler>(),
            workspaceClient.Object,
            Mock.Of<IUsageRateCardRepository>());

        var result = await service.GetAndProcessCheckoutSessionAsync("cs_test_buyer", buyerId, isSystemAdmin: false);

        Assert.True(result.IsSuccess);
        // The point of the test: no cross-service round trip stands between a paid session and
        // the buyer who is holding it.
        workspaceClient.Verify(
            c => c.VerifyWorkspaceRolesAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string[]>()),
            Times.Never);
    }

    // ── Renewal after expiry re-enables the workspace, immediately ────────────────────────────

    /// <summary>A handler standing in for SubscriptionPaymentEventHandler's renewal outcome.</summary>
    private sealed class RenewingHandler(Subscription renewed) : IPaymentEventHandler
    {
        public bool CanHandle(PaymentEventContext context) => true;

        public Task<Result> HandleAsync(PaymentEventContext context, CancellationToken cancellationToken = default)
        {
            context.Subscription = renewed;
            context.SubscriptionChanged = true;
            return Task.FromResult(Result.Success());
        }
    }

    /// <summary>
    /// A plan bought through checkout set only SubscriptionChanged, and entitlements were published
    /// for add-ons alone — so a workspace renewing after expiry kept a snapshot saying "no active
    /// subscription" and the WT-515 paywall refused the customer who had just paid, until the hourly
    /// reconcile. And the Redis 'subscription_expired' mark the expiry sweep wrote kept Start
    /// Translation refused for up to its 24h TTL. Both are cleared by the payment itself now.
    /// </summary>
    [Fact]
    public async Task ProcessPaymentEventAsync_Renewal_RepublishesEntitlementsAndLiftsTheAiSuspension()
    {
        var workspaceId = Guid.NewGuid();
        var renewed = new Subscription
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            IsActive = true,
            Status = SubscriptionConstants.SubscriptionStatuses.Active,
            ServiceState = SubscriptionConstants.ServiceStates.Healthy,
        };
        var entitlements = new Mock<IEntitlementChangePublisher>();
        var aiState = new Mock<IAiServiceStateStore>();
        aiState
            .Setup(s => s.SetAiServiceStateAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        var service = new PaymentAppService(
            _stripePaymentService.Object,
            _unitOfWork.Object,
            Mock.Of<ILogger<PaymentAppService>>(),
            _messagePublisher.Object,
            new IPaymentEventHandler[] { new RenewingHandler(renewed) },
            new Mock<IWorkspaceClient>().Object,
            Mock.Of<IUsageRateCardRepository>(),
            entitlements: entitlements.Object,
            aiServiceStateStore: aiState.Object);

        var request = CreateEvent(PaymentConstants.PaymentTypes.Subscription) with { WorkspaceIdStr = workspaceId.ToString() };
        var result = await service.ProcessPaymentEventAsync(request);

        Assert.True(result.IsSuccess, result.Error);
        entitlements.Verify(p => p.EnqueueAsync(
            workspaceId, EntitlementConstants.Reasons.SubscriptionChanged, It.IsAny<CancellationToken>()), Times.Once);
        aiState.Verify(s => s.SetAiServiceStateAsync(
            workspaceId, SubscriptionConstants.ServiceStates.Healthy, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    private PaymentAppService CreateService(params IPaymentEventHandler[] handlers)
        => new(
            _stripePaymentService.Object,
            _unitOfWork.Object,
            Mock.Of<ILogger<PaymentAppService>>(),
            _messagePublisher.Object,
            handlers,
            new Mock<IWorkspaceClient>().Object,
            Mock.Of<IUsageRateCardRepository>());

    private static StripePaymentEventRequest CreateEvent(string paymentType)
        => new(
            StripeSessionId: "cs_test_payment",
            PaymentIntentId: string.Empty,
            Amount: 12m,
            Currency: PaymentConstants.Currencies.Usd,
            UserIdStr: Guid.NewGuid().ToString(),
            WorkspaceIdStr: Guid.NewGuid().ToString(),
            PaymentType: paymentType,
            Status: PaymentConstants.PaymentStatuses.Paid);

    // ── backend#467: extra credits are sold only on top of a plan ─────────────────────────────

    [Theory]
    [InlineData(PaymentConstants.PaymentTypes.CreditTopUp)]
    [InlineData(PaymentConstants.PaymentTypes.CreditPack)]
    public async Task CreateCheckoutSessionAsync_RefusesExtraCredits_WithoutALiveSubscription_BeforeStripeIsCalled(string paymentType)
    {
        _subscriptionRepository
            .Setup(r => r.AnyAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await CreateService().CreateCheckoutSessionAsync(
            new CreateCheckoutSessionRequest(Guid.NewGuid(), Guid.NewGuid(), 0m, PaymentType: paymentType, Credits: 50_000,
                PackageId: Guid.NewGuid()));

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.BillingPurchaseRequiresSubscription, result.ErrorCode);
        _stripePaymentService.VerifyNoOtherCalls();
    }

    /// <summary>The gate asks about a LIVE plan: active, not deleted, its period not over.</summary>
    [Fact]
    public async Task CreateCheckoutSessionAsync_TheLiveSubscriptionTest_ExcludesAnEndedPeriod()
    {
        Expression<Func<Subscription, bool>>? asked = null;
        _subscriptionRepository
            .Setup(r => r.AnyAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), It.IsAny<CancellationToken>()))
            .Callback<Expression<Func<Subscription, bool>>, CancellationToken>((predicate, _) => asked = predicate)
            .ReturnsAsync(false);
        var workspaceId = Guid.NewGuid();

        await CreateService().CreateCheckoutSessionAsync(
            new CreateCheckoutSessionRequest(Guid.NewGuid(), workspaceId, 0m, PaymentType: PaymentConstants.PaymentTypes.CreditTopUp, Credits: 50_000));

        var test = asked!.Compile();
        Assert.True(test(new Subscription { WorkspaceId = workspaceId, IsActive = true, CurrentPeriodEnd = DateTime.UtcNow.AddDays(3) }));
        Assert.False(test(new Subscription { WorkspaceId = workspaceId, IsActive = true, CurrentPeriodEnd = DateTime.UtcNow.AddMinutes(-1) }));
        Assert.False(test(new Subscription { WorkspaceId = workspaceId, IsActive = false, CurrentPeriodEnd = DateTime.UtcNow.AddDays(3) }));
    }

    [Fact]
    public async Task CreateCheckoutSessionAsync_APlanCheckoutIsNotGatedOnHavingAPlan()
    {
        _stripePaymentService
            .Setup(s => s.CreateCheckoutSessionAsync(It.IsAny<CreateCheckoutSessionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success("https://checkout.stripe.test/s"));

        var result = await CreateService().CreateCheckoutSessionAsync(
            new CreateCheckoutSessionRequest(Guid.NewGuid(), Guid.NewGuid(), 100m, PaymentType: "Renewal", PlanSlug: "pro"));

        Assert.True(result.IsSuccess, result.Error);
        _subscriptionRepository.Verify(
            r => r.AnyAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}

