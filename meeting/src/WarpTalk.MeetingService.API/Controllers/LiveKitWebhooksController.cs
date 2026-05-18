using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using System.Linq;
using WarpTalk.MeetingService.Application.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.MeetingService.API.Controllers;

[ApiController]
[Route("api/v1/meetings/webhooks/livekit")]
public class LiveKitWebhooksController : ControllerBase
{
    private readonly IMeetingWebhookService _webhookService;
    private readonly string _apiSecret;

    public LiveKitWebhooksController(IMeetingWebhookService webhookService, IConfiguration config)
    {
        _webhookService = webhookService;
        _apiSecret = config["LiveKit:ApiSecret"] ?? throw new ArgumentNullException("LiveKit:ApiSecret");
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
        if (!ValidateWebhookToken(token, bodyText))
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

    private bool ValidateWebhookToken(string token, string bodyText)
    {
        try
        {
            var handler = new JwtSecurityTokenHandler();
            var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_apiSecret));
            
            handler.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = securityKey,
                ValidateIssuer = false,
                ValidateAudience = false,
                ValidateLifetime = false // Sometimes webhooks are slightly delayed
            }, out _);

            // Read the token to verify the body hash (sha256 of body mapped to 'sha256' claim)
            var jwtToken = handler.ReadJwtToken(token);
            var sha256Claim = jwtToken.Claims.FirstOrDefault(c => c.Type == "sha256")?.Value;

            if (string.IsNullOrEmpty(sha256Claim)) return true; // Some older LiveKit versions omit this

            using var sha256 = SHA256.Create();
            var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(bodyText));
            var computedHash = Convert.ToBase64String(hashBytes);

            return sha256Claim == computedHash;
        }
        catch
        {
            return false;
        }
    }
}
