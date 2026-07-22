using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Enums;

#if DEBUG

namespace WarpTalk.BillingService.API.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class StripeSimulationController : ControllerBase
{
    private readonly ICreditService _creditService;
    private readonly ILogger<StripeSimulationController> _logger;

    public StripeSimulationController(ICreditService creditService, ILogger<StripeSimulationController> logger)
    {
        _creditService = creditService;
        _logger = logger;
    }

    [HttpPost("webhook")]
    public async Task<IActionResult> HandleSimulatedWebhook([FromBody] StripeWebhookEvent request, CancellationToken ct)
    {
        var session = request.data.@object;
        if (session == null || !Guid.TryParse(session.client_reference_id, out var workspaceId))
        {
            return BadRequest(new { message = "Invalid or missing client_reference_id" });
        }

        _logger.LogInformation("Received simulated Stripe webhook for Workspace {WorkspaceId}. Session: {SessionId}, Status: {Status}",
            workspaceId, session.id, session.payment_status);

        if (request.type != "checkout.session.completed" || session.payment_status != "paid")
        {
            return BadRequest(new { message = "Simulation only supports a paid checkout.session.completed event for now." });
        }

        // Stripe amounts are in the smallest currency unit (cents for USD) — 1 cent = 1 credit.
        int creditsToTopUp = (int)session.amount_total;

        var result = await _creditService.TopUpCreditsAsync(
            workspaceId,
            new TopUpRequest(workspaceId, creditsToTopUp, CreditReferenceType.Transaction, Guid.NewGuid()),
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
#endif
