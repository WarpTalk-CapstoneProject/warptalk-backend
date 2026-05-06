using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;

namespace WarpTalk.BillingService.API.Controllers;

/// <summary>
/// Controller for simulating PayOS payment provider interactions.
/// This is isolated from the main BillingController to ensure safety.
/// </summary>
[ApiController]
[Route("api/v1/[controller]")]
public class PayOSSimulationController : ControllerBase
{
    private readonly IBillingService _billingService;
    private readonly ILogger<PayOSSimulationController> _logger;

    public PayOSSimulationController(IBillingService billingService, ILogger<PayOSSimulationController> logger)
    {
        _billingService = billingService;
        _logger = logger;
    }

    /// <summary>
    /// Simulates a successful payment webhook from PayOS.
    /// This will trigger a credit top-up for the specified workspace.
    /// </summary>
    [HttpPost("webhook/simulate-success")]
    public async Task<IActionResult> SimulatePayOSWebhook([FromBody] PayOSWebhookRequest request, [FromQuery] Guid workspaceId, CancellationToken ct)
    {
        _logger.LogInformation("Received simulated PayOS webhook for Workspace {WorkspaceId}. OrderCode: {OrderCode}, Status: {Status}", 
            workspaceId, request.data.orderCode, request.data.status);

        if (request.data.status != "PAID")
        {
            return BadRequest(new { message = "Simulation only supports PAID status for now." });
        }

        // Map PayOS amount to credits (e.g., 1000 VND = 1 Credit)
        int creditsToTopUp = request.data.amount / 1000;

        var result = await _billingService.TopUpCreditsAsync(
            workspaceId, 
            creditsToTopUp, 
            "Transaction", 
            Guid.NewGuid(), // Simulated Transaction Reference ID
            ct);

        if (!result.IsSuccess)
        {
            return StatusCode(500, new { message = "Failed to process simulated payment", error = result.Error });
        }

        return Ok(new 
        { 
            message = "Simulated payment processed successfully", 
            addedCredits = creditsToTopUp,
            newBalance = result.Value.CurrentCredits,
            payosData = request.data
        });
    }

    /// <summary>
    /// Helper to generate a valid-looking PayOS webhook payload for testing.
    /// </summary>
    [HttpGet("generate-test-payload")]
    public IActionResult GenerateTestPayload([FromQuery] int amount = 50000)
    {
        var data = new PayOSData(
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            amount,
            "Thanh toan nap credit WarpTalk",
            "PAID",
            "00",
            $"PL{Guid.NewGuid().ToString("N").Substring(0, 10)}"
        );

        var payload = new PayOSWebhookRequest(
            "00",
            "success",
            data,
            "SIMULATED_SIGNATURE_HASH"
        );

        return Ok(payload);
    }
}
