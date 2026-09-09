using System;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Application.DTOs;

namespace WarpTalk.TranslationRoomService.Application.Mappers;

public static class ArtifactMapper
{
    public static TranslationRoomArtifact ToEntity(this CreateArtifactRequest request)
    {
        var now = DateTime.UtcNow;

        return new TranslationRoomArtifact
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = request.RoomId,
            ArtifactType = request.ArtifactType ?? "TRANSCRIPT",
            FileUrl = request.FileUrl,
            FileFormat = request.FileFormat,
            FileSizeBytes = request.SizeBytes,
            Content = request.Content,
            ContainsRawAudio = request.ContainsRawAudio,
            ContainsRawVideo = request.ContainsRawVideo,
            ConsentRequired = request.ConsentRequired,
            RetentionUntil = request.RetentionUntil,
            Status = ArtifactStatus.Completed.ToString().ToUpperInvariant(),
            CreatedAt = now,
            // Equal to CreatedAt on a fresh artifact, so NULL keeps meaning "predates this column"
            // instead of becoming ambiguous with "written and never rewritten".
            UpdatedAt = now
        };
    }

    public static RoomArtifactDto ToDto(this TranslationRoomArtifact artifact)
    {
        return new RoomArtifactDto(
            artifact.Id,
            artifact.ArtifactType,
            artifact.FileFormat,
            artifact.FileSizeBytes,
            artifact.ContainsRawAudio,
            artifact.ContainsRawVideo,
            artifact.ConsentRequired,
            artifact.RetentionUntil,
            artifact.Status,
            artifact.CreatedAt
        );
    }
}
