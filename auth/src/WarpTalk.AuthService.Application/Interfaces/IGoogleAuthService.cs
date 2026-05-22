using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.AuthService.Application.Interfaces;

public interface IGoogleAuthService
{
    Task<Result<AuthResponse>> GoogleLoginAsync(GoogleLoginRequest request, CancellationToken ct = default);
    Task<Result> LinkGoogleAsync(Guid userId, LinkGoogleRequest request, CancellationToken ct = default);
    Task<Result> UnlinkGoogleAsync(Guid userId, CancellationToken ct = default);
}
