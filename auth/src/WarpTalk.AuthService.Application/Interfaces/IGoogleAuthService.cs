using WarpTalk.AuthService.Application.DTOs;

namespace WarpTalk.AuthService.Application.Interfaces;

public interface IGoogleAuthService
{
    Task<GoogleAuthPayload?> VerifyGoogleTokenAsync(string idToken, CancellationToken ct = default);
}
