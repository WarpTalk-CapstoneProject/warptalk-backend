namespace WarpTalk.AuthService.Application.DTOs;

/// <summary>
/// The two languages are optional and are the sign-up wizard's third step.
///
/// They are collected AT REGISTRATION rather than afterwards because self-registration issues no
/// session (BR-02 — the address has to be proven first), so there is no authenticated moment
/// between "account created" and "first meeting" in which the client could PUT them. Optional
/// because the Google and invited paths do not necessarily ask, and a missing answer must land on
/// the platform default rather than fail the registration.
/// </summary>
public record RegisterRequest(
    string Email,
    string Password,
    string FullName,
    string? DefaultSpeakLanguage = null,
    string? DefaultListenLanguage = null);

public record RegisterInvitedRequest(
    string Token,
    string Password,
    string FullName,
    string? DefaultSpeakLanguage = null,
    string? DefaultListenLanguage = null);

public record LoginRequest(string Email, string Password, string? IpAddress, string? DeviceInfo);

public record VerifyEmailRequest(string Token);

public record ForgotPasswordRequest(string Email);

/// <summary>
/// WT-597: asks for a fresh verification link by address, with no session.
///
/// Shaped exactly like <see cref="ForgotPasswordRequest"/> and answered exactly like it — 204
/// whatever the address turns out to be — because they leak the same thing if they are not.
/// </summary>
public record ResendVerificationRequest(string Email);

public record ResetPasswordRequest(string Token, string NewPassword);
