namespace WarpTalk.AuthService.Domain.Constants;

public static class VoiceProfileConstants
{
    public const int MaxDisplayNameLength = 100;
    public const int MaxProviderLength = 50;
    public const int MaxReferenceLength = 500;
    public const int MaxConsentTypeLength = 50;
    public const int MaxConsentTextVersionLength = 50;
    public const int MaxSampleTypeLength = 30;
    public const int MinSampleDurationSeconds = 1;
    public const int MaxSampleDurationSeconds = 600;

    public const string StatusDraft = "draft";
    public const string StatusPendingConsent = "pending_consent";
    public const string StatusTraining = "training";
    public const string StatusReady = "ready";
    public const string StatusFailed = "failed";
    public const string StatusDisabled = "disabled";

    public const string SampleTypeUploaded = "uploaded";
    public const string SampleTypeTraining = "training";
    public const string SampleTypeVerification = "verification";

    public const string ConsentTypeVoiceClone = "voice_clone";
}
