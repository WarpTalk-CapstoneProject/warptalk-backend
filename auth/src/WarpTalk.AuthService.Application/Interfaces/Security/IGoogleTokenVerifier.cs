using WarpTalk.AuthService.Application.DTOs;

namespace WarpTalk.AuthService.Application.Interfaces.Security;

public interface IGoogleTokenVerifier
{
    Task<GoogleAuthPayload?> VerifyGoogleTokenAsync(string idToken, CancellationToken ct = default);
}
