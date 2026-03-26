using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.AuthService.Application.Interfaces;

public interface IAuthService
{
    Task<Result<AuthResponse>> RegisterAsync(RegisterRequest request, CancellationToken ct = default);
    Task<Result<AuthResponse>> LoginAsync(LoginRequest request, CancellationToken ct = default);
    Task<Result<AuthResponse>> RefreshTokenAsync(RefreshTokenRequest request, CancellationToken ct = default);
    Task<Result> LogoutAsync(Guid userId, string refreshToken, CancellationToken ct = default);
    Task<Result<UserDto>> GetProfileAsync(Guid userId, CancellationToken ct = default);
    Task<Result<UserDto>> UpdateProfileAsync(Guid userId, UpdateProfileRequest request, CancellationToken ct = default);
    Task<Result<AuthResponse>> GoogleLoginAsync(GoogleLoginRequest request, CancellationToken ct = default);
    Task<Result> ChangePasswordAsync(Guid userId, ChangePasswordRequest request, CancellationToken ct = default);
}
