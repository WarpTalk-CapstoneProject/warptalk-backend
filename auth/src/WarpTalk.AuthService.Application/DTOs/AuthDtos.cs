namespace WarpTalk.AuthService.Application.DTOs;

public record RegisterRequest(string Email, string Password, string FullName);

public record LoginRequest(string Email, string Password, string? IpAddress, string? DeviceInfo);

public record AuthResponse(string AccessToken, string RefreshToken, DateTime ExpiresAt, UserDto User);

public record RefreshTokenRequest(string RefreshToken, string? IpAddress, string? DeviceInfo);

public record UserDto(
    Guid Id,
    string Email,
    string FullName,
    string? AvatarUrl,
    string? Phone,
    string? PreferredLanguage,
    string? Timezone,
    bool EmailVerified,
    IReadOnlyList<string> Roles
);

public record UpdateProfileRequest(string? FullName, string? Phone, string? PreferredLanguage, string? Timezone);

public record GoogleLoginRequest(string IdToken, string? IpAddress, string? DeviceInfo);

public record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public record GoogleAuthPayload(string Subject, string Email, string? Name, string? Picture, bool EmailVerified);
