using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using WarpTalk.BillingService.API.Controllers;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.Shared;
using Xunit;

namespace WarpTalk.BillingService.Tests;

public class StripeSimulationTests
{
    private readonly Mock<IBillingService> _billingServiceMock;
    private readonly Mock<ILogger<StripeSimulationController>> _loggerMock;
    private readonly StripeSimulationController _controller;

    public StripeSimulationTests()
    {
        _billingServiceMock = new Mock<IBillingService>();
        _loggerMock = new Mock<ILogger<StripeSimulationController>>();
        _controller = new StripeSimulationController(_billingServiceMock.Object, _loggerMock.Object);
    }

    [Fact]
    public async Task SimulateStripeWebhook_WithPaidCheckoutSession_ShouldTopUpCredits()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var amountTotal = 10000; // 10,000 cents = $100.00
        var expectedCredits = 10000;

        var session = new StripeCheckoutSession("cs_test_123", amountTotal, "usd", "paid", "pi_123", workspaceId.ToString());
        var request = new StripeWebhookEvent("evt_123", "checkout.session.completed", new StripeEventData(session));

        _billingServiceMock.Setup(x => x.TopUpCreditsAsync(
            workspaceId,
            expectedCredits,
            "Transaction",
            It.IsAny<Guid>(),
            It.IsAny<CancellationToken>(),
            null))
            .ReturnsAsync(Result.Success(new WorkspaceCreditsDto(workspaceId, 500, null, "Active")));

        // Act
        var result = await _controller.SimulateStripeWebhook(request, CancellationToken.None);

        // Assert
        Assert.IsType<OkObjectResult>(result);
        _billingServiceMock.Verify(x => x.TopUpCreditsAsync(
            workspaceId,
            expectedCredits,
            "Transaction",
            It.IsAny<Guid>(),
            It.IsAny<CancellationToken>(),
            null), Times.Once);
    }

    [Fact]
    public async Task SimulateStripeWebhook_WithUnpaidSession_ShouldReturnBadRequest()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var session = new StripeCheckoutSession("cs_test_123", 1000, "usd", "unpaid", null, workspaceId.ToString());
        var request = new StripeWebhookEvent("evt_123", "checkout.session.completed", new StripeEventData(session));

        // Act
        var result = await _controller.SimulateStripeWebhook(request, CancellationToken.None);

        // Assert
        Assert.IsType<BadRequestObjectResult>(result);
        _billingServiceMock.Verify(x => x.TopUpCreditsAsync(
            It.IsAny<Guid>(),
            It.IsAny<int>(),
            It.IsAny<string>(),
            It.IsAny<Guid?>(),
            It.IsAny<CancellationToken>(),
            It.IsAny<Guid?>()), Times.Never);
    }

    [Fact]
    public async Task SimulateStripeWebhook_WithInvalidClientReferenceId_ShouldReturnBadRequest()
    {
        // Arrange
        var session = new StripeCheckoutSession("cs_test_123", 1000, "usd", "paid", "pi_123", "not-a-guid");
        var request = new StripeWebhookEvent("evt_123", "checkout.session.completed", new StripeEventData(session));

        // Act
        var result = await _controller.SimulateStripeWebhook(request, CancellationToken.None);

        // Assert
        Assert.IsType<BadRequestObjectResult>(result);
    }
}
