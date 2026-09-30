using WarpTalk.TranslationRoomService.Application.Mappers;
using WarpTalk.TranslationRoomService.Domain.Entities;

namespace WarpTalk.TranslationRoomService.Application.Helpers;

/// <summary>
/// WT-870: whether a meeting keeps a post-meeting record — transcript, AI summary, minutes.
/// </summary>
/// <remarks>
/// <para>
/// "Save the meeting transcript" off means the meeting has no record afterwards, and the option's
/// own description says so: no transcript, no summary, no minutes. TranscriptService honoured the
/// transcript half (WT-587). The summary did not: live subtitles keep STT running, so
/// ai_assistant_worker kept accumulating every line, summarised it at meeting end, and the
/// finalizer saved that summary beside a transcript that did not exist.
/// </para>
/// <para>
/// ONE RULE, SEVERAL READERS. The finalizer, the late-summary recovery, the rewrite/rendering
/// endpoints and the summary-result consumer all have to agree, and a room they disagreed about
/// would get its summary through whichever door forgot to check.
/// </para>
/// <para>
/// Read from this service's own database, so there is no "older responder" case to fail open
/// for, unlike TranscriptService's gRPC read. <see cref="TranslationRoomMapper.ReadSettings"/>
/// resolves an absent key (and an unreadable blob) to TRUE, which is what every room created
/// before the field must keep.
/// </para>
/// </remarks>
public static class TranscriptRetention
{
    /// <summary>
    /// The error code a caller gets when it asks for a summary of a meeting that kept no
    /// transcript. Its own code rather than INVALID_STATE so the web can say why instead of
    /// showing a generic failure — and so it can match on something that is not a sentence.
    /// </summary>
    public const string ErrorCodeTranscriptNotSaved = "TRANSCRIPT_NOT_SAVED";

    public const string ErrorTranscriptNotSaved =
        "This meeting was held without saving its transcript, so it has no summary to generate.";

    /// <summary>True when the room keeps its transcript, and therefore its summary.</summary>
    public static bool IsSaved(TranslationRoom room) =>
        TranslationRoomMapper.ReadSettings(room.Settings).SaveTranscript;
}
