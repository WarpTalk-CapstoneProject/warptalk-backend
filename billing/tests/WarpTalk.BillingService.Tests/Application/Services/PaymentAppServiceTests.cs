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
}
