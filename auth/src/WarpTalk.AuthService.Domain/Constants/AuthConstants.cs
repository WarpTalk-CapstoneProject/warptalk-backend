namespace WarpTalk.AuthService.Domain.Constants;

public static class AuthConstants
{
    // Error messages
    public const string ErrorEmailExists = "Email already registered";
    public const string ErrorInvalidCredentials = "Invalid email or password";
    public const string ErrorAccountInactive = "Account is deactivated";
    public const string ErrorAccountLocked = "Account locked until {0}";
    public const string ErrorAccountLockedIndefinitely = "Account locked indefinitely";
    public const string ErrorInvalidToken = "Invalid or expired refresh token";
    public const string ErrorUserInactive = "User not found or inactive";
    public const string ErrorUserNotFound = "User not found";
    public const string ErrorInvalidPassword = "Invalid current password";
    public const string ErrorGoogleTokenInvalid = "Invalid Google token";
    public const string ErrorAccountPending = "Email not verified";
    public const string ErrorCooldownActive = "Too many requests. Please try again later.";
    public const string ErrorRateLimitExceeded = "Too many requests. Please try again later.";
    public const string ErrorEmailNotVerified = "Email is not verified. A new verification link has been sent to your email.";
    public const string ErrorGoogleEmailMismatch = "Google account email does not match the active user profile.";
    public const string ErrorUnlinkGoogleNoPassword = "Cannot unlink Google account without a local password set.";

    /// <summary>
    /// WT-596: shown when a dependency (database, cache) could not be reached.
    ///
    /// Worded as OUR problem and as retryable, because it is both. "An unexpected error occurred
    /// during login" was rendered straight into the toast beside a 400, which reads as a rejected
    /// credential — the one thing it never is.
    /// </summary>
    public const string ErrorServiceUnavailable =
        "WarpTalk is temporarily unavailable. This is not a problem with your details — please try again in a moment.";

    // Auth Settings Defaults
    public const int DefaultMaxFailedAttempts = 5;
    public const int DefaultLockoutDurationMinutes = 15;
    public const string DefaultRole = "user";
}
