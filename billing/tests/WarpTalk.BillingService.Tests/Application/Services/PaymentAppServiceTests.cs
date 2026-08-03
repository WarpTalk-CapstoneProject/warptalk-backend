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

    private PaymentAppService CreateService(params IPaymentEventHandler[] handlers)
        => new(
            _stripePaymentService.Object,
            _unitOfWork.Object,
            Mock.Of<ILogger<PaymentAppService>>(),
            _messagePublisher.Object,
            handlers);

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
