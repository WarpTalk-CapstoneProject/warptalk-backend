namespace WarpTalk.BillingService.Domain.Constants;

public static class UsageConstants
{
    public static class UsageTypes
    {
        public const string Summary = "summary";
        public const string VoiceCloning = "voice_cloning";
        public const string Chat = "chat";
        public const string TextToSpeech = "text_to_speech";
        public const string SpeechToText = "speech_to_text";
        public const string VoiceTranslation = "voice_translation";
        public const string AiAssistant = "ai_assistant";
        public const string DocumentTranslation = "document_translation";
    }

    public static class VoiceCloneCosts
    {
        public const int StandardProfile = 500;
        public const int AdvancedProfile = 800;
    }

    public static class UsageUnits
    {
        public const string Profile = "profile";
        public const string Token = "token";
        public const string Character = "character";
    }

    public static class UsageDetails
    {
        public const string AdvancedVoiceClone = "Advanced Voice Clone Profile";
        public const string StandardVoiceClone = "Standard Voice Clone Profile";
    }
}
