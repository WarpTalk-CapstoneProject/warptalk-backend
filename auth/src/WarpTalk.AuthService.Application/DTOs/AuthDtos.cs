namespace WarpTalk.AuthService.Application.DTOs;

public record RegisterRequest(string Email, string Password, string FullName);

public record LoginRequest(string Email, string Password, string? IpAddress, string? DeviceInfo);
