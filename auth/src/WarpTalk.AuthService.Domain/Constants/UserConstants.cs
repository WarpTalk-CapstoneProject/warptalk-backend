namespace WarpTalk.AuthService.Domain.Constants;

public static class UserConstants
{
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

    // Credential and Profile Length Constraints
    //
    // These exist so an over-long field is refused by validation instead of by Postgres. Without
    // them the string travels the whole way down and fails at SaveChangesAsync, where the only
    // thing the caller learns is that something went wrong on our side.

    /// <summary>Keep in sync with AuthDbContext: auth.users.full_name is varchar(150).</summary>
    public const int FullNameMaxLength = 150;

    /// <summary>RFC 5321's ceiling. The column is varchar(320), so this can never truncate.</summary>
    public const int EmailMaxLength = 255;

    public const int PasswordMinLength = 6;

    /// <summary>
    /// Not a security limit — PBKDF2's cost does not depend on input length. It is a bound on what
    /// a caller can make the server hold and hash, and it must be enforced on EVERY path that
    /// writes a password (register, register-invited, change, reset), not just on login. A cap on
    /// login alone would lock out anyone who had already set something longer.
    /// </summary>
    public const int PasswordMaxLength = 128;

    /// <summary>
    /// Generous headroom over what TokenHashing.GenerateToken actually produces (32 random bytes
    /// as base64url, so 43 characters), rather than a tight fit — the point is to refuse a payload
    /// nobody could have been issued, not to encode the current token size in a second place.
    /// </summary>
    public const int TokenMaxLength = 256;

    public const string ThemeLight = "light";
    public const string ThemeDark = "dark";
    public const string ThemeSystem = "system";

    public const string RoomTypeInstant = "instant";
    public const string RoomTypeScheduled = "scheduled";

    // Regex Patterns

    /// <summary>
    /// BCP-47 as this product uses it: "vi", "en-US", "zh-Hans-CN".
    ///
    /// Shape only, not a whitelist. The catalogue of what WarpTalk can translate lives in the web
    /// client and in the AI workers; duplicating it here would be a third copy that goes stale the
    /// first time a language is added. What this stops is a free-text field arriving in a column
    /// every meeting reads: anything that is not a language tag is rejected, and anything absent
    /// falls back to the platform default in UserSettingsMapper.
    ///
    /// The primary subtag is {2,3} because that is what real BCP-47 primary language subtags are —
    /// 4 is reserved and 5-8 is registered-but-unused. Allowing {2,8} let "english" through, which
    /// is exactly the sort of free text this rule exists to catch.
    ///
    /// One pattern, four validators. Registration used to accept "zh-Hans-CN" while settings —
    /// which held a separate, stricter ^[a-zA-Z]{2}(-[a-zA-Z]{2})?$ — rejected it, so a user could
    /// sign up with a language they could never afterwards save.
    /// </summary>
    public const string LanguageTagRegex = @"^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8}){0,2}$";
    public const string PermittedEmailRegex = @"^(?i)[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}$";
}
