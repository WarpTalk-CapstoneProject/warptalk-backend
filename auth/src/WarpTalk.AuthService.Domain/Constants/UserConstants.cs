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

    public const string ThemeLight = "light";
    public const string ThemeDark = "dark";
    public const string ThemeSystem = "system";

    public const string RoomTypeInstant = "instant";
    public const string RoomTypeScheduled = "scheduled";

    // Regex Patterns
    public const string LanguageCodeRegex = @"^[a-zA-Z]{2}(-[a-zA-Z]{2})?$";
    public const string PermittedEmailRegex = @"^(?i)[a-zA-Z0-9._%+-]+@gmail\.com$";
}
