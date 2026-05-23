using System;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using WarpTalk.PaymentService.Application.Services;
using WarpTalk.PaymentService.Application.Interfaces;
using WarpTalk.Shared.Protos;
using Xunit;

namespace WarpTalk.PaymentService.Tests.Application;

public class PaymentAppServiceTests
{
    private readonly Mock<IStripePaymentService> _stripePaymentServiceMock;
    private readonly Mock<BillingService.BillingServiceClient> _billingClientMock;
    private readonly PaymentAppService _sut; // System Under Test

    public PaymentAppServiceTests()
    {
        _stripePaymentServiceMock = new Mock<IStripePaymentService>();
        _billingClientMock = new Mock<BillingService.BillingServiceClient>();

        _sut = new PaymentAppService(
            _stripePaymentServiceMock.Object,
            _billingClientMock.Object
        );
    }

    [Fact]
    public async Task ProcessCheckoutSessionCompletedAsync_ShouldCallBillingClient()
    {
        // Arrange
        var stripeSessionId = "cs_test_123";
        var paymentIntentId = "pi_123";
        var amount = 10.0m;
        var currency = "usd";
        var userId = Guid.NewGuid().ToString();
        var paymentType = "CreditTopUp";

        _billingClientMock
            .Setup(c => c.ProcessPaymentSuccessAsync(It.IsAny<ProcessPaymentRequest>(), null, null, default))
            .Returns(new Grpc.Core.AsyncUnaryCall<ProcessPaymentResponse>(
                Task.FromResult(new ProcessPaymentResponse { Success = true }), 
                Task.FromResult(new Grpc.Core.Metadata()), 
                () => new Grpc.Core.Status(Grpc.Core.StatusCode.OK, ""), 
                () => new Grpc.Core.Metadata(), 
                () => { }));

        // Act
        await _sut.ProcessCheckoutSessionCompletedAsync(
            stripeSessionId, paymentIntentId, amount, currency, userId, paymentType
        );

        // Assert
        _billingClientMock.Verify(c => c.ProcessPaymentSuccessAsync(
            It.Is<ProcessPaymentRequest>(req => req.StripeSessionId == stripeSessionId && req.UserId == userId),
            null, null, default), Times.Once);
    }
}
