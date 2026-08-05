using System;

namespace WarpTalk.AuthService.Application.DTOs;

public record RefreshTokenRequest(string? RefreshToken, string? IpAddress, string? DeviceInfo);

public record LogoutRequest(string? RefreshToken);

public record AuthResponse(string AccessToken, string RefreshToken, DateTime ExpiresAt, UserDto User);
