using System;
using WarpTalk.TranscriptService.Application.DTOs;
using WarpTalk.TranscriptService.Domain.Entities;
using WarpTalk.TranscriptService.Domain.Enums;

namespace WarpTalk.TranscriptService.Application.Mappers;

public static class TranscriptCorrectionMapper
{
    public static TranscriptCorrectionDto ToDto(this TranscriptCorrection entity)
    {
        return new TranscriptCorrectionDto(
            entity.Id,
            entity.SegmentId,
            entity.UserId,
            entity.OriginalText,
            entity.CorrectedText,
            entity.CorrectionType,
            entity.Status,
            entity.TriggeredRetranslation,
            entity.ReviewedBy,
            entity.ReviewedAt,
            entity.CreatedAt
        );
    }

    public static TranscriptCorrection ToEntity(this CreateCorrectionDto dto, Guid segmentId)
    {
        return new TranscriptCorrection
        {
            Id = Guid.NewGuid(),
            SegmentId = segmentId,
            UserId = dto.UserId,
            OriginalText = dto.OriginalText,
            CorrectedText = dto.CorrectedText,
            CorrectionType = dto.CorrectionType,
            Status = CorrectionStatus.Pending,
            TriggeredRetranslation = true,
            CreatedAt = DateTime.UtcNow
        };
    }
}
