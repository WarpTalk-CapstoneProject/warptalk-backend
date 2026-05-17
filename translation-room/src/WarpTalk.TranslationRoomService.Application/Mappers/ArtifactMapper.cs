using System;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Application.DTOs;

namespace WarpTalk.TranslationRoomService.Application.Mappers;

public static class ArtifactMapper
{
    public static TranslationRoomArtifact ToEntity(CreateArtifactRequest request)
    {
        return new TranslationRoomArtifact
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = request.RoomId,
            ArtifactType = request.ArtifactType,
            FileUrl = request.FileUrl,
            FileFormat = request.FileFormat,
            FileSizeBytes = request.SizeBytes,
            ContainsRawAudio = request.ContainsRawAudio,
            ContainsRawVideo = request.ContainsRawVideo,
            ConsentRequired = request.ConsentRequired,
            RetentionUntil = request.RetentionUntil,
            Status = ArtifactStatus.Completed.ToString().ToUpperInvariant(), 
            CreatedAt = DateTime.UtcNow
        };
    }

    public static RoomArtifactDto ToArtifactDto(TranslationRoomArtifact artifact)
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
