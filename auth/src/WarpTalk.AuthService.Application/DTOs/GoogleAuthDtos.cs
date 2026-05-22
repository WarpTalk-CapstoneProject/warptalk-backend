namespace WarpTalk.AuthService.Application.DTOs;

public record GoogleLoginRequest(string IdToken, string? IpAddress, string? DeviceInfo);

public record GoogleAuthPayload(string Subject, string Email, string? Name, string? Picture, bool EmailVerified);

public record LinkGoogleRequest(string IdToken);
