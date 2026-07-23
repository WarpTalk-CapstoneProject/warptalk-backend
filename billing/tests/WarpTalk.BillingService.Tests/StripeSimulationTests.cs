using WarpTalk.BillingService.Domain.Constants;
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
    private readonly Mock<ICreditService> _creditServiceMock;
    private readonly Mock<ILogger<StripeSimulationController>> _loggerMock;
    private readonly StripeSimulationController _controller;

    public StripeSimulationTests()
    {
        _creditServiceMock = new Mock<ICreditService>();
        _loggerMock = new Mock<ILogger<StripeSimulationController>>();
        _controller = new StripeSimulationController(_creditServiceMock.Object, _loggerMock.Object);
    }

    [Fact]
    public async Task SimulateStripeWebhook_WithValidPaidSession_ShouldTopUpCredits()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var amountTotal = 10000; // 10,000 units
        var expectedCredits = 10000;

        var session = new StripeCheckoutSession("cs_test_123", amountTotal, "usd", "paid", "pi_123", workspaceId.ToString());
        var request = new StripeWebhookEvent("evt_123", "checkout.session.completed", new StripeEventData(session));

        // After constants refactoring, ReferenceType is BillingConstants.ReferenceTypes.Payment ("payment")
        _creditServiceMock.Setup(x => x.TopUpCreditsAsync(
            workspaceId,
            It.Is<TopUpRequest>(r => r.Amount == expectedCredits && r.ReferenceType == BillingConstants.ReferenceTypes.Payment),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new CreditBalanceDto(workspaceId, 500, 0, 500, "Active", DateTime.UtcNow, DateTime.UtcNow.AddMonths(1))));

        // Act
        var result = await _controller.HandleSimulatedWebhook(request, CancellationToken.None);

        // Assert
        Assert.IsType<OkObjectResult>(result);
        _creditServiceMock.Verify(x => x.TopUpCreditsAsync(
            workspaceId,
            It.Is<TopUpRequest>(r => r.Amount == expectedCredits && r.ReferenceType == BillingConstants.ReferenceTypes.Payment),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SimulateStripeWebhook_WithUnpaidSession_ShouldReturnBadRequest()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var session = new StripeCheckoutSession("cs_test_123", 1000, "usd", "unpaid", null, workspaceId.ToString());
        var request = new StripeWebhookEvent("evt_123", "checkout.session.completed", new StripeEventData(session));

        // Act
        var result = await _controller.HandleSimulatedWebhook(request, CancellationToken.None);

        // Assert
        Assert.IsType<BadRequestObjectResult>(result);
        _creditServiceMock.Verify(x => x.TopUpCreditsAsync(
            It.IsAny<Guid>(),
            It.IsAny<TopUpRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SimulateStripeWebhook_WithInvalidClientReferenceId_ShouldReturnBadRequest()
    {
        // Arrange
        var session = new StripeCheckoutSession("cs_test_123", 1000, "usd", "paid", "pi_123", "not-a-guid");
        var request = new StripeWebhookEvent("evt_123", "checkout.session.completed", new StripeEventData(session));

        // Act
        var result = await _controller.HandleSimulatedWebhook(request, CancellationToken.None);

        // Assert
        Assert.IsType<BadRequestObjectResult>(result);
    }
}
