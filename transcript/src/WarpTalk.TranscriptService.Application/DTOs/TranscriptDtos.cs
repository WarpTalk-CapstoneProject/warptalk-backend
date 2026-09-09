using System;
using System.Collections.Generic;

namespace WarpTalk.TranscriptService.Application.DTOs;

public record PagedResult<T>(
    int TotalCount,
    IEnumerable<T> Items
);

public record TranscriptDto(
    Guid Id,
    Guid WorkspaceId,
    Guid TranslationRoomId,
    int Version,
    string Status,
    string SourceLanguage,
    int TotalSegments,
    int TotalDurationMs,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? FinalizedAt,
    /// <summary>
    /// WT-473: the UTC instant <c>TranscriptSegmentDto.StartTimeMs</c> is measured from — the
    /// first audio chunk the STT pipeline saw.
    ///
    /// The other half of the pair is <c>translation_room_artifacts.recording_started_at</c>;
    /// together they are what makes "click a transcript line, seek the recording" possible at all.
    /// NULL for transcripts predating the column, and a consumer must read NULL as CANNOT ALIGN.
    /// Substituting CreatedAt would be off by however long the meeting waited for its first word,
    /// which renders as a plausible seek to the wrong place.
    /// </summary>
    DateTime? TimelineAnchorAt = null
);

public record TranscriptSegmentDto(
    Guid Id,
    Guid? SpeakerParticipantId,
    string SpeakerName,
    string OriginalText,
    string OriginalLanguage,
    decimal? Confidence,
    long StartTimeMs,
    long EndTimeMs,
    int SequenceOrder,
    /// <summary>A human has corrected this line. The client shows it, and uses it to tell whether
    /// a summary written earlier is now behind the record.</summary>
    bool IsCorrected = false,
    /// <summary>When the row last changed — moved by a correction.</summary>
    DateTime? UpdatedAt = null
);

public record TranscriptTranslationDto(
    Guid Id,
    Guid SegmentId,
    string TargetLanguage,
    string TranslatedText,
    string TranslatorModel,
    // WT-278: the source segment's STT confidence, not a translation quality score. NULL when the
    // source segment carried none.
    decimal? SourceSttConfidence,
    bool IsRetranslated,
    int? LatencyMs
);

/// <summary>
/// How much of a transcript can actually be read in one language.
///
/// The live pipeline only ever produces the target language that was selected at that moment, so
/// a meeting whose speakers switched targets mid-way has a different subset translated for each
/// language — and a meeting where translation was never started has none at all. The reader's
/// language picker was built from whatever happened to exist, which made every choice partial:
/// picking English still showed the Japanese stretch in Japanese. These counts are what a client
/// needs to say "this language is short by N lines" and to ask for those N to be filled in.
/// </summary>
/// <param name="TargetLanguage">Bare ISO-639-1, as stored on SegmentTranslationLink.</param>
/// <param name="TotalSegments">Real segments — control markers such as __MEETING_END__ excluded.</param>
/// <param name="SpokenInTarget">Segments already spoken in this language; their original text IS the answer.</param>
/// <param name="Translated">Segments carrying a current translation link into this language.</param>
/// <param name="Missing">Segments that are neither — the backfill's work list.</param>
/// <param name="Status">idle | running | complete | failed. "complete" is decided by Missing == 0,
/// not by the run marker, so a finished backfill stops reading as running the moment its last line lands.</param>
public record TranscriptLanguageCoverageDto(
    string TargetLanguage,
    int TotalSegments,
    int SpokenInTarget,
    int Translated,
    int Missing,
    string Status
);

/// <summary>Body of POST .../translations/backfill.</summary>
public record BackfillTranscriptLanguageRequest(string TargetLanguage);

/// <summary>
/// WT-605. One [Pause Transcript, Resume Transcript] window — display metadata for the
/// transcript panel's "Transcript paused · HH:MM–HH:MM" divider. <see cref="EndedAt"/> is null
/// while the transcript is currently paused for this room.
/// </summary>
public record TranscriptPauseWindowDto(
    Guid Id,
    Guid TranslationRoomId,
    DateTime StartedAt,
    DateTime? EndedAt
);
