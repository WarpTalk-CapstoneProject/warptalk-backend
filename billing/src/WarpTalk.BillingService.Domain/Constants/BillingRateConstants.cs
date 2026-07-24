namespace WarpTalk.BillingService.Domain.Constants;

public static class BillingRateConstants
{
    public const string SectionName = "BillingRates";

    public static class Keys
    {
        public const string SttPerSecond = "SttPerSecond";
        public const string TranslationPer100Chars = "TranslationPer100Chars";
        public const string StandardTtsPerSecond = "StandardTtsPerSecond";
        public const string VoiceClonePerSecond = "VoiceClonePerSecond";
        public const string AiAssistantInputPer1000Tokens = "AiAssistantInputPer1000Tokens";
        public const string AiAssistantOutputPer1000Tokens = "AiAssistantOutputPer1000Tokens";

        public const string FullSttPerSecond = "BillingRates:SttPerSecond";
        public const string FullTranslationPer100Chars = "BillingRates:TranslationPer100Chars";
        public const string FullStandardTtsPerSecond = "BillingRates:StandardTtsPerSecond";
        public const string FullVoiceClonePerSecond = "BillingRates:VoiceClonePerSecond";
        public const string FullAiAssistantInputPer1000Tokens = "BillingRates:AiAssistantInputPer1000Tokens";
        public const string FullAiAssistantOutputPer1000Tokens = "BillingRates:AiAssistantOutputPer1000Tokens";
    }

    public static class Defaults
    {
        public const double SttPerSecond = 1.0;
        public const double TranslationPer100Chars = 1.0;
        public const double StandardTtsPerSecond = 1.0;
        public const double VoiceClonePerSecond = 1.5;
        public const double AiAssistantInputPer1000Tokens = 0.5;
        public const double AiAssistantOutputPer1000Tokens = 2.0;
    }
}
