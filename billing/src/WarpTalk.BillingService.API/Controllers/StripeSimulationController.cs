using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;

namespace WarpTalk.BillingService.API.Controllers;

/// <summary>
/// Controller for simulating Stripe payment provider interactions.
/// This is isolated from the main BillingController to ensure safety.
/// </summary>
[ApiController]
[Route("api/v1/[controller]")]
public class StripeSimulationController : ControllerBase
{
    private readonly IBillingService _billingService;
    private readonly ILogger<StripeSimulationController> _logger;

    public StripeSimulationController(IBillingService billingService, ILogger<StripeSimulationController> logger)
    {
        _billingService = billingService;
        _logger = logger;
    }

    /// <summary>
    /// Simulates a "checkout.session.completed" webhook event from Stripe.
    /// This will trigger a credit top-up for the workspace named in client_reference_id.
    /// </summary>
    [HttpPost("webhook/simulate-success")]
    public async Task<IActionResult> SimulateStripeWebhook([FromBody] StripeWebhookEvent request, CancellationToken ct)
    {
        var session = request.data.@object;

        if (!Guid.TryParse(session.client_reference_id, out var workspaceId))
        {
            return BadRequest(new { message = "client_reference_id must be a valid workspace id." });
        }

        _logger.LogInformation("Received simulated Stripe webhook for Workspace {WorkspaceId}. Session: {SessionId}, Status: {Status}",
            workspaceId, session.id, session.payment_status);

        if (request.type != "checkout.session.completed" || session.payment_status != "paid")
        {
            return BadRequest(new { message = "Simulation only supports a paid checkout.session.completed event for now." });
        }

        // Stripe amounts are in the smallest currency unit (cents for USD) — 1 cent = 1 credit.
        int creditsToTopUp = (int)session.amount_total;

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
            stripeData = session
        });
    }

    /// <summary>
    /// Helper to generate a valid-looking Stripe checkout.session.completed webhook payload for testing.
    /// </summary>
    [HttpGet("generate-test-payload")]
    public IActionResult GenerateTestPayload([FromQuery] long amountTotal = 5000, [FromQuery] Guid? workspaceId = null)
    {
        var session = new StripeCheckoutSession(
            $"cs_test_{Guid.NewGuid().ToString("N").Substring(0, 20)}",
            amountTotal,
            "usd",
            "paid",
            $"pi_{Guid.NewGuid().ToString("N").Substring(0, 20)}",
            (workspaceId ?? Guid.NewGuid()).ToString()
        );

        var payload = new StripeWebhookEvent(
            $"evt_{Guid.NewGuid().ToString("N").Substring(0, 20)}",
            "checkout.session.completed",
            new StripeEventData(session)
        );

        return Ok(payload);
    }
}
