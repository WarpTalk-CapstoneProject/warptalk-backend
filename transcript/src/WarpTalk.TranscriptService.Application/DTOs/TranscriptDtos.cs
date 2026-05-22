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
    int SequenceOrder
);

public record TranscriptTranslationDto(
    Guid Id,
    Guid SegmentId,
    string TargetLanguage,
    string TranslatedText,
    string TranslatorModel,
    decimal? Confidence,
    bool IsRetranslated,
    int? LatencyMs
);
