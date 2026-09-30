using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using Moq;
using WarpTalk.MeetingService.API.Controllers;
using WarpTalk.MeetingService.Application.Interfaces;
using WarpTalk.MeetingService.Application.Services;
using WarpTalk.MeetingService.Domain.Interfaces;

namespace WarpTalk.MeetingService.Tests.API.Controllers;

/// <summary>
/// WT-824 / WT-660 — the LiveKit webhook 401, at the layer it actually happened.
///
/// LiveKit puts the signed JWT in the Authorization header AS IS — no "Bearer " scheme. Its own
/// sender (livekit/protocol webhook/url_notifier.go: <c>r.Header.Set("Authorization", token)</c>)
/// and its own verifier (webhook/verifier.go reads the header and hands it straight to
/// <c>ParseAPIToken</c>) both agree on that. The controller refused anything not starting with
/// "Bearer ", before the signature was ever looked at — so every genuine delivery, egress_ended
/// included, was answered 401 "Missing or invalid Authorization header", and only the 2-minute
/// reconciliation sweep ever completed a recording.
///
/// WebhookTokenAcceptanceTests could not see this: it calls ValidateWebhookToken with the bare
/// token, which is the step AFTER the prefix check. These tests go through the controller with the
/// header exactly as LiveKit sends it.
/// </summary>
public class LiveKitWebhooksControllerAuthorizationTests
{
    private const string Secret = "test-webhook-secret-with-at-least-32-characters";
    private const string Body = """{"event":"egress_started","egressInfo":{"egressId":"EG_x"}}""";

    [Fact]
    public async Task AcceptsTheTokenExactlyAsLiveKitSendsIt()
    {
        var result = await PostAsync(Body, authorization: TokenFor(Body));

        Assert.IsType<OkResult>(result);
    }

    [Fact]
    public async Task StillAcceptsABearerPrefixedToken()
    {
        var result = await PostAsync(Body, authorization: "Bearer " + TokenFor(Body));

        Assert.IsType<OkResult>(result);
    }

    [Fact]
    public async Task RefusesADeliveryWithNoAuthorizationHeader()
    {
        var result = await PostAsync(Body, authorization: null);

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    /// <summary>Accepting the raw form must not mean accepting anything: the signature still decides.</summary>
    [Fact]
    public async Task StillRefusesARawTokenSignedWithAnotherSecret()
    {
        var result = await PostAsync(
            Body,
            authorization: TokenFor(Body, secret: "another-secret-that-is-also-at-least-32-chars"));

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task StillRefusesARawTokenWhoseBodyChanged()
    {
        var result = await PostAsync(
            """{"event":"egress_started","egressInfo":{"egressId":"EG_tampered"}}""",
            authorization: TokenFor(Body));

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    private static async Task<IActionResult> PostAsync(string body, string? authorization)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["LiveKit:ApiSecret"] = Secret })
            .Build();

        var service = new MeetingWebhookService(
            Mock.Of<IUnitOfWork>(),
            Mock.Of<IRedisService>(),
            Mock.Of<IEgressCompletion>(),
            config,
            NullLogger<MeetingWebhookService>.Instance);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "POST";
        httpContext.Request.ContentType = "application/webhook+json";
        httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        if (authorization != null)
            httpContext.Request.Headers.Authorization = authorization;

        var controller = new LiveKitWebhooksController(service, NullLogger<LiveKitWebhooksController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };

        return await controller.HandleWebhook();
    }

    /// <summary>A token shaped the way LiveKit signs one: HS256, iss = API key, sha256 of the body.</summary>
    private static string TokenFor(string body, string secret = Secret)
    {
        var token = new JwtSecurityToken(
            issuer: "APIfakekeyfortests",
            claims: new[]
            {
                new Claim("sha256", Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(body)))),
            },
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),
                SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
