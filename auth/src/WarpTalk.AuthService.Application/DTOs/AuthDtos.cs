namespace WarpTalk.AuthService.Application.DTOs;

public record RegisterRequest(string Email, string Password, string FullName);

public record RegisterInvitedRequest(string Token, string Password, string FullName);

public record LoginRequest(string Email, string Password, string? IpAddress, string? DeviceInfo);

public record VerifyEmailRequest(string Token);

public record ForgotPasswordRequest(string Email);

public record ResetPasswordRequest(string Token, string NewPassword);
