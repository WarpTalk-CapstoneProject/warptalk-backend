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

public class MeetingWebhookServiceSecurityTests
{
    private const string Secret = "test-webhook-secret-with-at-least-32-characters";
    private const string Body = """{"event":"room_started"}""";

    [Fact]
    public void ValidateWebhookToken_RejectsTokenWithoutBodyHash()
    {
        var sut = CreateService();
        var token = CreateToken(expires: DateTime.UtcNow.AddMinutes(5), bodyHash: null);

        Assert.False(sut.ValidateWebhookToken(token, Body));
    }

    [Fact]
    public void ValidateWebhookToken_RejectsExpiredToken()
    {
        var sut = CreateService();
        var token = CreateToken(
            expires: DateTime.UtcNow.AddMinutes(-5),
            bodyHash: Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(Body))));

        Assert.False(sut.ValidateWebhookToken(token, Body));
    }

    private static MeetingWebhookService CreateService()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LiveKit:ApiSecret"] = Secret
            })
            .Build();
        // These tests are about token validation, which runs before any event is dispatched, so
        // the completion collaborator is never reached.
        return new MeetingWebhookService(
            Mock.Of<IUnitOfWork>(),
            Mock.Of<IRedisService>(),
            Mock.Of<IEgressCompletion>(),
            config,
            NullLogger<MeetingWebhookService>.Instance);
    }

    private static string CreateToken(DateTime expires, string? bodyHash)
    {
        var claims = bodyHash == null
            ? Array.Empty<Claim>()
            : new[] { new Claim("sha256", bodyHash) };
        var token = new JwtSecurityToken(
            claims: claims,
            expires: expires,
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)),
                SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
