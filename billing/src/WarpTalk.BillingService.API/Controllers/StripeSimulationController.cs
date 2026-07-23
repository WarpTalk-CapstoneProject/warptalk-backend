using Microsoft.AspNetCore.Mvc;
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
            return HandleFailure(ErrorCodes.BillingSimulationInvalidRequest, ApiMessageConstants.ErrorMessages.BillingSimulationInvalidClientRef);
        }

        if (request.type != BillingConstants.StripeEvents.CheckoutSessionCompleted || session.payment_status != BillingConstants.Payments.StatusPaid)
        {
            return HandleFailure(ErrorCodes.BillingSimulationInvalidRequest, ApiMessageConstants.ErrorMessages.BillingSimulationInvalidEvent);
        }

        // Stripe amounts are in the smallest currency unit (cents for USD) — 1 cent = 1 credit.
        int creditsToTopUp = (int)session.amount_total;

        var result = await _creditService.TopUpCreditsAsync(
            workspaceId,
            new TopUpRequest(workspaceId, creditsToTopUp, BillingConstants.ReferenceTypes.Payment, Guid.NewGuid()),
            ct);

        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);
        //TODO : Add the logging for the success case. log the request and response 
        return Ok(new SimulatedPaymentResponse(
            BillingConstants.SuccessMessages.SimulatePaymentMessage,
            creditsToTopUp,
            result.Value.CurrentCredits,
            session
        ));
    }
    //TODO : Add the logging for the success case. log the request and response 
    [HttpGet("generate-test-payload")]
    public IActionResult GenerateTestPayload([FromQuery] long amountTotal = 5000, [FromQuery] Guid? workspaceId = null)
    {
        var session = new StripeCheckoutSession(
            $"{BillingConstants.StripeSimulation.SessionPrefix}{Guid.NewGuid().ToString("N").Substring(0, 20)}",
            amountTotal,
            BillingConstants.Currencies.Usd,
            BillingConstants.Payments.StatusPaid,
            $"{BillingConstants.StripeSimulation.PaymentIntentPrefix}{Guid.NewGuid().ToString("N").Substring(0, 20)}",
            (workspaceId ?? Guid.NewGuid()).ToString()
        );

        var payload = new StripeWebhookEvent(
            $"{BillingConstants.StripeSimulation.EventPrefix}{Guid.NewGuid().ToString("N").Substring(0, 20)}",
            BillingConstants.StripeEvents.CheckoutSessionCompleted,
            new StripeEventData(session)
        );

        return Ok(payload);
    }

    private ActionResult HandleFailure(string? errorCode, string? error) =>
        BadRequest(new ApiErrorResponse(error ?? ApiMessageConstants.ErrorMessages.BillingInternalError, errorCode ?? ErrorCodes.InternalServerError));
}
#endif
