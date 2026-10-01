namespace WarpTalk.TranscriptService.Domain;

/// <summary>
/// The values of <c>transcript_segments.far_speaker_source</c> this service writes itself: who
/// decided which Google Meet participant a bridge stand-in segment belongs to. Live values come
/// from warptalk-ai's stt_worker (<c>far_speaker_source</c> on stt:results) and are stored as sent;
/// the post-meeting relabel overrides them.
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
