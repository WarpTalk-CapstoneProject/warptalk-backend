namespace WarpTalk.Shared;

public static class ApiMessageConstants
{
    public static class ErrorMessages
    {
        // Common API ProblemDetails Titles & Details
        public const string ValidationFailedTitle = "Validation Failed";
        public const string UnauthorizedTokenDetail = "Could not extract a valid user ID from the authentication token.";
    }

    public static class ValidationMessages
    {
        public const string TitleRequired = "Title is required.";
        public const string TitleMaxLength = "Title cannot exceed 255 characters.";

        // Auth & Profiles
        public const string EmailRequired = "Email is required.";
        public const string EmailInvalidFormat = "Email must be a valid @gmail.com address.";
        public const string PasswordRequired = "Password is required.";
        public const string PasswordMinLength = "Password must be at least 6 characters long.";
        public const string FullNameRequired = "Full name is required.";
        public const string FullNameNotEmpty = "Full name cannot be empty.";
        public const string RefreshTokenRequired = "Refresh token is required.";
        public const string GoogleIdTokenRequired = "Google ID token is required.";
        public const string PreferredLanguageInvalid = "Preferred language format is invalid.";
        public const string TimezoneInvalid = "Mã IANA timezone không hợp lệ.";
        public const string NewPasswordRequired = "New password is required.";
        public const string NewPasswordMinLength = "New password must be at least 6 characters long.";

        // User Settings
        public const string FontSizeOutOfBounds = "Font size must be between {0} and {1}.";
        public const string MaxParticipantsOutOfBounds = "Default max participants must be between {0} and {1}.";
        public const string InvalidTheme = "Invalid theme. Supported: {0}, {1}, {2}.";
        public const string InvalidRoomType = "Invalid translation room type.";
        public const string InvalidSpeakLanguage = "Invalid default speak language format.";
        public const string InvalidListenLanguage = "Invalid default listen language format.";

        // Voice Profiles
        public const string VoiceDisplayNameMaxLength = "Voice profile display name cannot exceed 100 characters.";
        public const string VoiceProviderMaxLength = "Voice provider cannot exceed 50 characters.";
        public const string VoiceReferenceMaxLength = "Voice reference cannot exceed 500 characters.";
        public const string VoiceStatusInvalid = "Invalid voice profile status.";
        public const string VoiceSampleTypeInvalid = "Invalid voice sample type.";
        public const string VoiceSampleFileUrlRequired = "Voice sample file URL is required.";
        public const string VoiceSampleDurationOutOfBounds = "Voice sample duration must be between {0} and {1} seconds.";
        public const string VoiceSampleLanguageInvalid = "Voice sample language format is invalid.";
        public const string VoiceConsentTypeInvalid = "Invalid voice consent type.";
        public const string VoiceConsentTextVersionRequired = "Voice consent text version is required.";
    }
}
