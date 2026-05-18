using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using WarpTalk.MeetingService.Application.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.MeetingService.Infrastructure.Services;

public class LiveKitTokenService : ILiveKitTokenService
{
    private readonly string _apiKey;
    private readonly string _apiSecret;

    public LiveKitTokenService(IConfiguration configuration)
    {
        _apiKey = configuration["LiveKit:ApiKey"] ?? throw new ArgumentNullException("LiveKit:ApiKey");
        _apiSecret = configuration["LiveKit:ApiSecret"] ?? throw new ArgumentNullException("LiveKit:ApiSecret");
    }

    public Result<string> GenerateToken(string roomName, string participantIdentity, string participantName, bool canPublish, bool canSubscribe)
    {
        try
        {
            var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_apiSecret));
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new Claim("name", participantName)
        };

        // LiveKit Video Grant structure
        // { "video": { "roomJoin": true, "room": "roomName", "canPublish": true, "canSubscribe": true } }
        var videoGrant = new Dictionary<string, object>
        {
            { "roomJoin", true },
            { "room", roomName },
            { "canPublish", canPublish },
            { "canSubscribe", canSubscribe }
        };

        var payloadDict = new Dictionary<string, object>
        {
            { "iss", _apiKey },
            { "sub", participantIdentity },
            { "video", videoGrant }
        };

        var header = new JwtHeader(credentials);
        var payload = new JwtPayload(
            issuer: _apiKey,
            audience: null,
            claims: claims,
            notBefore: null,
            expires: DateTime.UtcNow.AddHours(4) // Token expires in 4 hours
        );

        // Add custom grants including 'sub' if not already added by JwtPayload
        foreach (var kvp in payloadDict)
        {
            if (kvp.Key != "iss") // iss is already added by JwtPayload constructor
            {
                payload.Add(kvp.Key, kvp.Value);
            }
        }

        var token = new JwtSecurityToken(header, payload);
        var handler = new JwtSecurityTokenHandler();
        return Result.Success(handler.WriteToken(token));
        }
        catch (Exception ex)
        {
            return Result.Failure<string>($"Failed to generate LiveKit token: {ex.Message}", "TOKEN_GENERATION_FAILED");
        }
    }
}
