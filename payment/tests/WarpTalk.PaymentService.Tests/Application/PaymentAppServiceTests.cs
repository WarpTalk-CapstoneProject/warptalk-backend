using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Grpc.Core;
using Moq;
using WarpTalk.PaymentService.Application.DTOs;
using WarpTalk.PaymentService.Application.Interfaces;
using WarpTalk.PaymentService.Application.Services;
using WarpTalk.Shared.Protos;
using Xunit;

namespace WarpTalk.PaymentService.Tests.Application;

public class PaymentAppServiceTests
{
    private readonly Mock<IStripePaymentService> _mockStripePaymentService;
    private readonly Mock<BillingService.BillingServiceClient> _mockBillingClient;
    private readonly PaymentAppService _service;

    public PaymentAppServiceTests()
    {
        _mockStripePaymentService = new Mock<IStripePaymentService>();
        _mockBillingClient = new Mock<BillingService.BillingServiceClient>();
        
        _service = new PaymentAppService(_mockStripePaymentService.Object, _mockBillingClient.Object);
    }

    [Fact]
    public async Task CreateCheckoutSessionAsync_ShouldReturnSessionUrl()
    {
        // Arrange
        var request = new CreateCheckoutSessionRequest
        {
            UserId = Guid.NewGuid(),
            WorkspaceId = Guid.NewGuid(), // Add this
            Amount = 10m,
            Currency = "usd",
            PaymentType = "Credits"
        };
        var expectedUrl = "https://checkout.stripe.com/test";
        
        _mockStripePaymentService.Setup(x => x.CreateCheckoutSessionAsync(
                request.UserId, request.WorkspaceId, request.Amount, request.Currency, request.PaymentType))
            .ReturnsAsync(expectedUrl);

        // Act
        var result = await _service.CreateCheckoutSessionAsync(request);

        // Assert
        result.Should().Be(expectedUrl);
        _mockStripePaymentService.Verify(x => x.CreateCheckoutSessionAsync(
            request.UserId, request.WorkspaceId, request.Amount, request.Currency, request.PaymentType), Times.Once);
    }

    [Fact]
    public async Task CreateCheckoutSessionAsync_ShouldThrowException_WhenWorkspaceIdIsEmpty()
    {
        // Arrange
        var request = new CreateCheckoutSessionRequest
        {
            UserId = Guid.NewGuid(),
            WorkspaceId = Guid.Empty, // Invalid
            Amount = 10m,
            Currency = "usd",
            PaymentType = "Credits"
        };

        // Act
        Func<Task> act = async () => await _service.CreateCheckoutSessionAsync(request);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*WorkspaceId is required.*");
    }

    [Fact]
    public async Task ProcessPaymentEventAsync_ShouldCallBillingGrpcService()
    {
        // Arrange
        var stripeSessionId = "cs_test_123";
        var paymentIntentId = "pi_test_123";
        var amount = 10m;
        var currency = "usd";
        var userId = Guid.NewGuid().ToString();
        var workspaceId = Guid.NewGuid().ToString();
        var paymentType = "Credits";
        var status = "success";
        var failureReason = "";

        var expectedResponse = new ProcessPaymentResponse { Success = true };

        // Setup mock for gRPC client
        _mockBillingClient.Setup(x => x.ProcessPaymentEventAsync(
                It.IsAny<ProcessPaymentEventRequest>(), null, null, CancellationToken.None))
            .Returns(new AsyncUnaryCall<ProcessPaymentResponse>(
                Task.FromResult(expectedResponse),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { }));

        // Act
        await _service.ProcessPaymentEventAsync(stripeSessionId, paymentIntentId, amount, currency, userId, workspaceId, paymentType, status, failureReason);

        // Assert
        _mockBillingClient.Verify(x => x.ProcessPaymentEventAsync(
            It.Is<ProcessPaymentEventRequest>(req => 
                req.UserId == userId &&
                req.WorkspaceId == workspaceId &&
                req.Amount == 10.0 &&
                req.StripeSessionId == stripeSessionId &&
                req.ProviderTransactionId == paymentIntentId &&
                req.Status == status
            ), null, null, CancellationToken.None), Times.Once);
    }
}
