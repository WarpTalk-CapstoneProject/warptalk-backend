namespace WarpTalk.TranscriptService.Domain;

/// <summary>
/// The values of <c>transcript_segments.far_speaker_source</c>: who decided which Google Meet
/// participant a bridge stand-in segment belongs to.
/// </summary>
public static class FarSpeakerSources
{
    /// <summary>Aligned automatically against Google Meet's own speaker-attributed transcript.</summary>
    public const string GoogleTranscript = "google_transcript";

    /// <summary>
    /// Chosen by the host. Always wins: the automatic relabel never overwrites a host's choice.
    /// </summary>
    public const string Host = "host";
}
