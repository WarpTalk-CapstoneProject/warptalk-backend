namespace WarpTalk.AuthService.Domain.Constants;

/// <summary>WT-888 — what happened when a recording was checked against its challenge phrase.</summary>
public static class VoiceEnrollmentOutcomes
{
    /// <summary>The recording said the phrase closely enough; it became a voice profile.</summary>
    public const string Passed = "passed";

    /// <summary>The recording was transcribed and did not say the phrase.</summary>
    public const string Mismatch = "mismatch";

    /// <summary>The recording could not be transcribed (provider down or not configured).</summary>
    public const string TranscriptionFailed = "transcription_failed";
}

/// <summary>
/// WT-888 — the stable codes the create-profile endpoint answers with when the live-recording
/// check refuses a sample. The web translates them; the English message is the fallback.
/// </summary>
public static class VoiceEnrollmentErrorCodes
{
    /// <summary>No challenge id at all: an arbitrary file upload, which is no longer accepted.</summary>
    public const string ChallengeRequired = "VOICE_CHALLENGE_REQUIRED";

    /// <summary>Unknown, someone else's, already used, expired, or issued for another language.</summary>
    public const string ChallengeInvalid = "VOICE_CHALLENGE_INVALID";

    /// <summary>The recording did not say the phrase.</summary>
    public const string ChallengeMismatch = "VOICE_CHALLENGE_MISMATCH";
}
