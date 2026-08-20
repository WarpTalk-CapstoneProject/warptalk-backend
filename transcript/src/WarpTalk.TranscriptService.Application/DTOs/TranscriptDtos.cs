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
    DateTime? FinalizedAt
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
