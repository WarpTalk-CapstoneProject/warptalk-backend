using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.Shared;
#if DEBUG

namespace WarpTalk.BillingService.API.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class StripeSimulationController : ControllerBase
{
    private readonly ICreditGrantService _creditGrantService;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<StripeSimulationController> _logger;

    public StripeSimulationController(
        ICreditGrantService creditGrantService,
        IWebHostEnvironment environment,
        ILogger<StripeSimulationController> logger)
    {
        _creditGrantService = creditGrantService;
        _environment = environment;
        _logger = logger;
    }

    [HttpPost("webhook")]
    public async Task<IActionResult> HandleSimulatedWebhook([FromBody] StripeWebhookEvent request, CancellationToken ct)
    {
        if (!_environment.IsDevelopment())
        {
            return NotFound();
        }

        var session = request.data.@object;
        if (session == null || !Guid.TryParse(session.client_reference_id, out var workspaceId))
        {
            return BadRequest(new ApiErrorResponse(ApiMessageConstants.ErrorMessages.BillingSimulationInvalidClientRef, ErrorCodes.BillingSimulationInvalidRequest));
        }

        if (request.type != PaymentConstants.StripeEvents.CheckoutSessionCompleted || session.payment_status != PaymentConstants.Payments.StatusPaid)
        {
            return BadRequest(new ApiErrorResponse(ApiMessageConstants.ErrorMessages.BillingSimulationInvalidEvent, ErrorCodes.BillingSimulationInvalidRequest));
        }

        // Stripe amounts are in the smallest currency unit (cents for USD) — 1 cent = 1 credit.
        int creditsToTopUp = (int)session.amount_total;

        var result = await _creditGrantService.GrantCreditsAsync(
            workspaceId,
            new TopUpRequest(workspaceId, creditsToTopUp, TransactionConstants.ReferenceTypes.Payment, Guid.NewGuid()),
            ct);

        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }
        //TODO : Add the logging for the success case. log the request and response 
        return Ok(new SimulatedPaymentResponse(
            BillingMessageConstants.SuccessMessages.SimulatePaymentMessage,
            creditsToTopUp,
            result.Value!.CurrentCredits,
            session
        ));
    }
    //TODO : Add the logging for the success case. log the request and response 
    [HttpGet("generate-test-payload")]
    public IActionResult GenerateTestPayload([FromQuery] long amountTotal = 5000, [FromQuery] Guid? workspaceId = null)
    {
        if (!_environment.IsDevelopment())
        {
            return NotFound();
        }

        var session = new StripeCheckoutSession(
            $"{PaymentConstants.StripeSimulation.SessionPrefix}{Guid.NewGuid().ToString("N").Substring(0, 20)}",
            amountTotal,
            PaymentConstants.Currencies.Usd,
            PaymentConstants.Payments.StatusPaid,
            $"{PaymentConstants.StripeSimulation.PaymentIntentPrefix}{Guid.NewGuid().ToString("N").Substring(0, 20)}",
            (workspaceId ?? Guid.NewGuid()).ToString()
        );

        var payload = new StripeWebhookEvent(
            $"{PaymentConstants.StripeSimulation.EventPrefix}{Guid.NewGuid().ToString("N").Substring(0, 20)}",
            PaymentConstants.StripeEvents.CheckoutSessionCompleted,
            new StripeEventData(session)
        );

        return Ok(payload);
    }
}
#endif
