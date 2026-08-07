namespace WarpTalk.AuthService.Domain.Settings;

using WarpTalk.AuthService.Domain.Constants;
using WarpTalk.AuthService.Domain.Settings;

public class AuthSettings
{
    public int MaxFailedAttempts { get; set; } = AuthConstants.DefaultMaxFailedAttempts;
    public int LockoutDurationMinutes { get; set; } = AuthConstants.DefaultLockoutDurationMinutes;
    public string DefaultRole { get; set; } = AuthConstants.DefaultRole;
    public int VerificationTokenLifetimeMinutes { get; set; } = 60;
    public int PasswordResetTokenLifetimeMinutes { get; set; } = 30;
    /// <summary>
    /// When true, a self-registered account is marked email-verified without the user ever
    /// proving they control the address.
    ///
    /// This must default to <c>false</c>. It was defaulting to <c>true</c> and was set nowhere in
    /// any configuration file, so the spec-137 anti-takeover guard was off everywhere including
    /// production. That guard is the <c>!user.EmailVerified</c> branch in
    /// <c>GoogleAuthService.GoogleLoginAsync</c> ("Safe Matching Rule"): it refuses to link a
    /// Google identity to a pre-existing password account whose address was never verified.
    /// Auto-verifying every self-registration means that branch can never be reached, so anyone
    /// could register an address they do not control and have it treated as proven.
    ///
    /// Turning it on is a deliberate, environment-scoped decision — see appsettings.json, where
    /// it is now written out explicitly rather than inherited from a default.
    /// </summary>
    public bool AutoVerifySelfRegistration { get; set; } = false;
}
