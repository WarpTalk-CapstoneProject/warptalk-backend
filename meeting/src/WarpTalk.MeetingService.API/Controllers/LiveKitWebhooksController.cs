using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using WarpTalk.MeetingService.Application.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.MeetingService.API.Controllers;

[ApiController]
[Route("api/v1/meetings/webhooks/livekit")]
public class LiveKitWebhooksController : ControllerBase
{
    private readonly IMeetingWebhookService _webhookService;
    private readonly ILogger<LiveKitWebhooksController> _logger;

    public LiveKitWebhooksController(
        IMeetingWebhookService webhookService,
        ILogger<LiveKitWebhooksController> logger)
    {
        _webhookService = webhookService;
        _logger = logger;
    }

    [HttpPost]
    [AllowAnonymous] // Verification is done via LiveKit token in header
    public async Task<IActionResult> HandleWebhook()
    {
        // 1. Read Body
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var bodyText = await reader.ReadToEndAsync();

        // 2. Validate Signature
        var token = ReadWebhookToken(Request.Headers.Authorization.ToString());
        if (token == null)
        {
            // Said out loud: this refusal happens before ValidateWebhookToken, so none of that
            // method's four named refusals (WT-660) can ever explain it.
            _logger.LogWarning("Rejected a LiveKit webhook: it carries no Authorization token.");
            return Unauthorized(new ApiErrorResponse("Missing or invalid Authorization header", ErrorCodes.Unauthorized));
        }

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

    /// <summary>
    /// WT-824: the webhook token, from the Authorization header in the form LiveKit actually sends.
    ///
    /// LiveKit sets the header to the bare JWT — no scheme. Its sender
    /// (livekit/protocol <c>webhook/url_notifier.go</c>: <c>r.Header.Set("Authorization", token)</c>)
    /// and its own receiver (<c>webhook/verifier.go</c> hands the header straight to
    /// <c>ParseAPIToken</c>) agree on that, as does every server SDK's <c>WebhookReceiver</c>.
    ///
    /// This used to demand a "Bearer " prefix and refuse anything else before the signature was
    /// ever checked, so every genuine delivery — egress_ended included — was answered 401 "Missing
    /// or invalid Authorization header" (~105 of 108 a day on production, WT-660). Only the
    /// 2-minute reconciliation sweep ever completed a recording, and it logged nothing that pointed
    /// here, because the refusal happened one layer above the method that WT-660 taught to explain
    /// itself.
    ///
    /// A "Bearer " prefix is still accepted (case-insensitively), so a proxy or a hand-written test
    /// client that adds one keeps working. Either way the token then has to pass the full signature,
    /// expiry and body-hash check in <see cref="IMeetingWebhookService.ValidateWebhookToken"/>; nothing
    /// about accepting the raw form loosens what is accepted.
    /// </summary>
    internal static string? ReadWebhookToken(string? authorizationHeader)
    {
        if (string.IsNullOrWhiteSpace(authorizationHeader)) return null;

        var value = authorizationHeader.Trim();
        const string bearer = "Bearer ";
        if (value.StartsWith(bearer, StringComparison.OrdinalIgnoreCase))
            value = value[bearer.Length..].Trim();

        return value.Length == 0 ? null : value;
    }
}
