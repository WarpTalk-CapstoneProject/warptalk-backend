using Google.Apis.Auth;
using Microsoft.Extensions.Configuration;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.AuthService.Application.DTOs;

namespace WarpTalk.AuthService.Infrastructure.Security;

public class GoogleAuthService : IGoogleAuthService
{
    private readonly string _clientId;

    public GoogleAuthService(IConfiguration configuration)
    {
        _clientId = configuration["Authentication:Google:ClientId"] 
            ?? throw new InvalidOperationException("Google ClientId is not configured.");
    }

    public async Task<GoogleAuthPayload?> VerifyGoogleTokenAsync(string idToken, CancellationToken ct = default)
    {
        try
        {
            var settings = new GoogleJsonWebSignature.ValidationSettings
            {
                Audience = new[] { _clientId }
            };

            var payload = await GoogleJsonWebSignature.ValidateAsync(idToken, settings);
            if (payload == null) return null;

            return new GoogleAuthPayload(
                payload.Subject,
                payload.Email,
                payload.Name,
                payload.Picture,
                payload.EmailVerified
            );
        }
        catch (InvalidJwtException)
        {
            return null;
        }
    }
}
