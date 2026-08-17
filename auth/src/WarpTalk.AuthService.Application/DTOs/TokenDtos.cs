using System;

namespace WarpTalk.AuthService.Application.DTOs;

public record RefreshTokenRequest(string? RefreshToken, string? IpAddress, string? DeviceInfo);

public record LogoutRequest(string? RefreshToken);

public record AuthResponse(string AccessToken, string RefreshToken, DateTime ExpiresAt, UserDto User);

/// <summary>
/// BR-02 — what self-registration returns, which is not always a session.
///
/// Registration used to hand back a full <see cref="AuthResponse"/> unconditionally and the
/// controller wrote auth cookies from it, so a brand-new account was signed in before its email
/// had been verified. Login already refused an unverified account (UserStatusHelper treats it as
/// pending), so the two paths disagreed: the door was locked and the window was open.
///
/// <paramref name="Auth"/> is null exactly when verification is still outstanding. A nullable
/// field rather than an AuthResponse full of empty strings — a caller that forgets to check gets
/// a null reference at the cookie write, not a session made of blanks.
/// </summary>
public record RegisterResponse(bool EmailVerificationRequired, AuthResponse? Auth);
