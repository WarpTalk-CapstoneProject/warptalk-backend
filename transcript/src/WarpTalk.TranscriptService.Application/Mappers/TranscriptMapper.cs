using WarpTalk.TranscriptService.Application.DTOs;
using WarpTalk.TranscriptService.Domain.Entities;

namespace WarpTalk.TranscriptService.Application.Mappers;

public static class TranscriptMapper
{
    public static TranscriptDto ToDto(this Transcript entity)
    {
        return new TranscriptDto(
            entity.Id,
            entity.WorkspaceId,
            entity.TranslationRoomId,
            entity.Version,
            entity.Status.ToString().ToLowerInvariant(),
            entity.SourceLanguage,
            entity.TotalSegments,
            entity.TotalDurationMs,
            entity.CreatedAt,
            entity.UpdatedAt,
            entity.FinalizedAt
        );
    }

    public static TranscriptSegmentDto ToDto(this TranscriptSegment entity)
    {
        return new TranscriptSegmentDto(
            entity.Id,
            entity.SpeakerParticipantId,
            entity.SpeakerName,
            entity.OriginalText,
            entity.OriginalLanguage,
            entity.Confidence,
            entity.StartTimeMs,
            entity.EndTimeMs,
            entity.SequenceOrder
        );
    }

    public static TranscriptTranslationDto ToDto(this TranscriptTranslation entity)
    {
        return new TranscriptTranslationDto(
            entity.Id,
            entity.SegmentId,
            entity.TargetLanguage,
            entity.TranslatedText,
            entity.TranslatorModel,
            entity.Confidence,
            entity.IsRetranslated,
            entity.LatencyMs
        );
    }
}
