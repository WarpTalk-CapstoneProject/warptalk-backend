using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using WarpTalk.BillingService.API.Controllers;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.Shared;
using Xunit;

namespace WarpTalk.BillingService.Tests;

public class PayOSSimulationTests
{
    private readonly Mock<IBillingService> _billingServiceMock;
    private readonly Mock<ILogger<PayOSSimulationController>> _loggerMock;
    private readonly PayOSSimulationController _controller;

    public PayOSSimulationTests()
    {
        _billingServiceMock = new Mock<IBillingService>();
        _loggerMock = new Mock<ILogger<PayOSSimulationController>>();
        _controller = new PayOSSimulationController(_billingServiceMock.Object, _loggerMock.Object);
    }

    [Fact]
    public async Task SimulatePayOSWebhook_WithValidPaidStatus_ShouldTopUpCredits()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var amount = 100000; // 100,000 VND
        var expectedCredits = 100; // 100,000 / 1000

        var payosData = new PayOSData(12345, amount, "Test", "PAID", "00", "LNK123");
        var request = new PayOSWebhookRequest("00", "success", payosData, "SIG");

        _billingServiceMock.Setup(x => x.TopUpCreditsAsync(
            workspaceId, 
            expectedCredits, 
            "Transaction", 
            It.IsAny<Guid>(), 
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new WorkspaceCreditsDto(workspaceId, 500, null, "Active")));

        // Act
        var result = await _controller.SimulatePayOSWebhook(request, workspaceId, CancellationToken.None);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        _billingServiceMock.Verify(x => x.TopUpCreditsAsync(
            workspaceId, 
            expectedCredits, 
            "Transaction", 
            It.IsAny<Guid>(), 
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SimulatePayOSWebhook_WithUnpaidStatus_ShouldReturnBadRequest()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var payosData = new PayOSData(12345, 10000, "Test", "PENDING", "00", "LNK123");
        var request = new PayOSWebhookRequest("00", "success", payosData, "SIG");

        // Act
        var result = await _controller.SimulatePayOSWebhook(request, workspaceId, CancellationToken.None);

        // Assert
        Assert.IsType<BadRequestObjectResult>(result);
        _billingServiceMock.Verify(x => x.TopUpCreditsAsync(
            It.IsAny<Guid>(), 
            It.IsAny<int>(), 
            It.IsAny<string>(), 
            It.IsAny<Guid?>(), 
            It.IsAny<CancellationToken>()), Times.Never);
    }
}
