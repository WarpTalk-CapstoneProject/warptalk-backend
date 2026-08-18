namespace WarpTalk.AuthService.Domain.Constants;

/// <summary>
/// What produced a <see cref="Entities.VoiceProfile"/>. Written to a varchar column, so string
/// constants rather than a C# enum — the database already owns the vocabulary, and duplicating it
/// in two type systems is how the two come to disagree.
/// </summary>
public static class VoiceProfileSources
{
    /// <summary>A recording the person deliberately made and uploaded. Never replaced automatically.</summary>
    public const string Upload = "upload";

    /// <summary>
    /// Captured and cloned while they spoke in a meeting. Replaced by a later capture that scores
    /// better by TTS_VOICE_CLONE_UPGRADE_MARGIN — which is the whole mechanism by which the clone
    /// gets closer to the person over time.
    /// </summary>
    public const string InMeeting = "in_meeting";
}
