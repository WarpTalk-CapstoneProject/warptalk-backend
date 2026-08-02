namespace WarpTalk.BillingService.Domain.Constants;

public static class HelperConstants
{
    public static class Audit
    {
        public const string Enabled = "enabled";
        public const string Disabled = "disabled";
    }

    public static class Concurrency
    {
        public const string ExceptionName = "DbUpdateConcurrencyException";
        public const string ConcurrencyLogTemplate = "Concurrency conflict for WorkspaceId {WorkspaceId}. Attempt {Attempt} of {MaxRetries}";
        public const string ErrorLogTemplate = "Error executing operation for WorkspaceId {WorkspaceId}";
        public const int DefaultMaxRetries = 3;
        public const int BaseDelayMilliseconds = 50;
    }

    public static class CreditRates
    {
        public static class ReferenceTypes
        {
            public const string Summary = "Summary";
            public const string VoiceCloning = "VoiceCloning";
            public const string Chat = "Chat";
            public const string TTS = "TTS";
            public const string STT = "STT";
            public const string AiSpeechTranslation = "AiSpeechTranslation";
            public const string Translation = "Translation";
        }

        public static class MediaStreamTypes
        {
            public const string Audio = "audio";
            public const string VideoSd = "video_sd";
            public const string VideoHd = "video_hd";
        }

        public static class Rates
        {
            public const double Audio = 0.2;
            public const double VideoSd = 0.5;
            public const double VideoHd = 1.0;
            public const double SecondsPerMinute = 60.0;
            public const double BlockMinutes = 15.0;
            public const double DefaultVideoSd = 0.5;
        }
    }
}
