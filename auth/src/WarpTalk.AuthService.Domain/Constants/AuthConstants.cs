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

    // Auth Settings Defaults
    public const int DefaultMaxFailedAttempts = 5;
    public const int DefaultLockoutDurationMinutes = 15;
    public const string DefaultRole = "user";

    // User Settings Defaults
    public const string DefaultSpeakLanguage = "vi-VN";
    public const string DefaultListenLanguage = "en-US";
    public const string DefaultTranslationRoomType = "instant";
    public const string DefaultTheme = "system";
    public const int DefaultTranscriptFontSize = 14;
    public const int DefaultMaxParticipants = 10;
    public const bool DefaultVoiceCloneEnabled = false;
    public const bool DefaultMicNoiseSuppression = true;
    public const bool DefaultAutoRecordTranslationRooms = false;
    public const bool DefaultAutoGenerateSummary = true;
    public const bool DefaultShowOriginalTranscript = true;
    public const bool DefaultShowTranslatedTranscript = true;
    public const bool DefaultHighContrast = false;
    public const bool DefaultScreenReaderMode = false;

    // User Settings Validation Constraints
    public const int MinTranscriptFontSize = 10;
    public const int MaxTranscriptFontSize = 32;
    public const int MinMaxParticipants = 1;
    public const int MaxMaxParticipants = 500;

    public const string ThemeLight = "light";
    public const string ThemeDark = "dark";
    public const string ThemeSystem = "system";

    public const string RoomTypeInstant = "instant";
    public const string RoomTypeScheduled = "scheduled";

    // Regex Patterns
    public const string LanguageCodeRegex = @"^[a-zA-Z]{2}(-[a-zA-Z]{2})?$";
}

