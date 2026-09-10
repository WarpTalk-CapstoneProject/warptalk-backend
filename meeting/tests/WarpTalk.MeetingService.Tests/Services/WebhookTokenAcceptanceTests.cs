using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using Moq;
using WarpTalk.MeetingService.Application.Interfaces;
using WarpTalk.MeetingService.Application.Services;
using WarpTalk.MeetingService.Domain.Interfaces;

namespace WarpTalk.MeetingService.Tests.Services;

/// <summary>
/// WT-660: the webhook token check, from the ACCEPTING side.
///
/// MeetingWebhookServiceSecurityTests pins two refusals — a token with no body hash, and an
/// expired one. Nothing pinned that a correctly signed LiveKit webhook is let through, and on
/// production every egress_ended delivery is being answered 401: ~105 of 108 in 24h, which is why
/// only the reconciliation sweep has ever completed a recording.
///
/// A suite that only tests refusals passes just as happily when the answer is "refuse everything",
/// so these tests exist to make that impossible to ship again. They also fix the shape of the
/// token LiveKit actually sends — an `iss` of the API key, and a body hash over the exact bytes —
/// so a future change to the validator has to keep working against it.
/// </summary>
public class WebhookTokenAcceptanceTests
{
    private const string Secret = "test-webhook-secret-with-at-least-32-characters";
    /// <summary>Shape only — LiveKit key ids start "API". Never the real one: it identifies a
    /// production project, and the secret-scan gate rejects it on sight, correctly.</summary>
    private const string ApiKey = "APIfakekeyfortests";

    [Fact]
    public void AcceptsACorrectlySignedWebhook()
    {
        var body = """{"event":"egress_ended","egressInfo":{"egressId":"EG_x"}}""";

        Assert.True(CreateService().ValidateWebhookToken(TokenFor(body), body));
    }

    /// <summary>
    /// LiveKit signs with `iss` set to the API key. The validator turns issuer checking off, so
    /// this must make no difference — but "must make no difference" is worth one test, because the
    /// alternative is discovering it against production.
    /// </summary>
    [Fact]
    public void AcceptsATokenCarryingTheApiKeyAsIssuer()
    {
        var body = """{"event":"room_started"}""";

        Assert.True(CreateService().ValidateWebhookToken(TokenFor(body, issuer: ApiKey), body));
    }

    /// <summary>
    /// The realistic payload: participant display names in this product are Vietnamese, so the
    /// body is multi-byte UTF-8. The controller reads it with `new StreamReader(Request.Body,
    /// Encoding.UTF8)` and the validator hashes `Encoding.UTF8.GetBytes(bodyText)` — a round trip
    /// that is only lossless while both halves agree, and a mismatch here would show up as exactly
    /// the blanket 401 production is seeing.
    /// </summary>
    [Fact]
    public void AcceptsABodyWithNonAsciiNames()
    {
        var body = """{"event":"participant_joined","participant":{"name":"Huỳnh Thái Tú"}}""";

        Assert.True(CreateService().ValidateWebhookToken(TokenFor(body), body));
    }

    /// <summary>
    /// An egress_ended payload is far larger than the other events — the whole EgressInfo. If
    /// something in the chain truncated or re-encoded a big body, small events would pass and this
    /// one would not, which is the shape of "recording is the only thing that never completes".
    /// </summary>
    [Fact]
    public void AcceptsALargeEgressPayload()
    {
        var body =
            """{"event":"egress_ended","egressInfo":{"egressId":"EG_x","roomName":"room-1","status":"EGRESS_COMPLETE","fileResults":[{"filename":"""
            + $"\"{new string('a', 4096)}\"" + """}]}}""";

        Assert.True(CreateService().ValidateWebhookToken(TokenFor(body), body));
    }

    /// <summary>
    /// The check that gives the whole suite its teeth: a body that changed in transit must still
    /// be refused. Without this, "accepts everything" would pass every test above.
    /// </summary>
    [Fact]
    public void StillRefusesABodyThatDoesNotMatchItsHash()
    {
        var signed = """{"event":"egress_ended","egressInfo":{"egressId":"EG_x"}}""";
        var delivered = """{"event":"egress_ended","egressInfo":{"egressId":"EG_tampered"}}""";

        Assert.False(CreateService().ValidateWebhookToken(TokenFor(signed), delivered));
    }

    private static MeetingWebhookService CreateService()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LiveKit:ApiSecret"] = Secret
            })
            .Build();

        return new MeetingWebhookService(
            Mock.Of<IUnitOfWork>(),
            Mock.Of<IRedisService>(),
            Mock.Of<IEgressCompletion>(),
            config,
            NullLogger<MeetingWebhookService>.Instance);
    }

    /// <summary>A token signed the way LiveKit signs one: HS256, `sha256` claim over the body.</summary>
    private static string TokenFor(string body, string? issuer = null)
    {
        var token = new JwtSecurityToken(
            issuer: issuer,
            claims: new[]
            {
                new Claim("sha256", Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(body)))),
            },
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)),
                SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
