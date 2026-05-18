using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using WarpTalk.MeetingService.Application.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.MeetingService.API.Controllers;

[ApiController]
[Route("api/v1/meetings/webhooks/livekit")]
public class LiveKitWebhooksController : ControllerBase
{
    private readonly IMeetingWebhookService _webhookService;

    public LiveKitWebhooksController(IMeetingWebhookService webhookService)
    {
        _webhookService = webhookService;
    }

    [HttpPost]
    [AllowAnonymous] // Verification is done via LiveKit token in header
    public async Task<IActionResult> HandleWebhook()
    {
        // 1. Read Body
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var bodyText = await reader.ReadToEndAsync();

        // 2. Validate Signature
        var authHeader = Request.Headers["Authorization"].ToString();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer "))
            return Unauthorized(new ApiErrorResponse("Missing or invalid Authorization header", ErrorCodes.Unauthorized));

        var token = authHeader.Substring("Bearer ".Length);
        if (!_webhookService.ValidateWebhookToken(token, bodyText))
            return Unauthorized(new ApiErrorResponse("Invalid webhook signature", ErrorCodes.Unauthorized));

        // 3. Process Event
        using var doc = JsonDocument.Parse(bodyText);
        var result = await _webhookService.ProcessWebhookAsync(doc.RootElement);

        if (!result.IsSuccess)
        {
            if (result.ErrorCode == ErrorCodes.ValidationError)
                return BadRequest(new ApiErrorResponse(result.Error ?? "Validation Error", result.ErrorCode));
            return StatusCode(500, new ApiErrorResponse(result.Error ?? "Unknown error", result.ErrorCode));
        }

        return Ok();
    }

}
