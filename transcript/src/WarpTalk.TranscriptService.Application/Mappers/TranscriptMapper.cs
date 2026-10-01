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
            entity.FinalizedAt,
            entity.TimelineAnchorAt
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
            entity.SequenceOrder,
            entity.IsCorrected,
            entity.UpdatedAt,
            entity.CleanText,
            entity.CleanFlags ?? Array.Empty<string>()
        );
    }

    public static TranscriptCleanSentenceDto ToDto(this TranscriptCleanSentence entity)
    {
        return new TranscriptCleanSentenceDto(
            entity.Id,
            entity.SpeakerParticipantId,
            entity.SegmentIds,
            entity.CleanText,
            entity.Language,
            entity.Flags,
            entity.Source,
            entity.Revision,
            entity.UpdatedAt
        );
    }

    public static TranscriptPauseWindowDto ToDto(this TranscriptPauseWindow entity)
    {
        return new TranscriptPauseWindowDto(
            entity.Id,
            entity.TranslationRoomId,
            entity.StartedAt,
            entity.EndedAt
        );
    }

}
