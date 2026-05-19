using System;
using System.Collections.Generic;
using System.Linq;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.ValueObjects;

namespace WarpTalk.TranslationRoomService.Application.Mappers;

public static class TranslationRoomMapper
{
    public static TranslationRoomDto ToResponseDto(this TranslationRoom room)
    {
        if (string.IsNullOrEmpty(room.Settings))
        {
            throw new InvalidOperationException($"TranslationRoom {room.Id} is missing Settings configuration in the database.");
        }

        RoomSettingsResponse settings = System.Text.Json.JsonSerializer.Deserialize<RoomSettingsResponse>(
            room.Settings, 
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException($"Failed to deserialize Settings for TranslationRoom {room.Id}");

        return new TranslationRoomDto(
            room.Id,
            room.WorkspaceId,
            room.HostId,
            room.Title,
            room.Description,
            room.TranslationRoomCode,
            room.Status,
            Enum.Parse<TranslationRoomType>(room.TranslationRoomType, true),
            room.MaxParticipants,
            room.SourceLanguage,
            Helpers.LanguageHelper.ParseTargetLanguages(room.TargetLanguages),
            room.ScheduledAt,
            room.StartedAt,
            room.EndedAt,
            room.CreatedAt,
            settings
        );
    }

    public static TranslationRoom ToEntity(this CreateTranslationRoomRequest request, Guid hostId, string roomCode, string status, string sourceLanguage, List<string> targetLanguages)
    {
        return new TranslationRoom
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = request.WorkspaceId ?? Guid.Empty,
            HostId = hostId,
            Title = request.Title,
            Description = request.Description,
            TranslationRoomCode = roomCode,
            Status = status,
            TranslationRoomType = request.TranslationRoomType.ToString(),
            MaxParticipants = request.MaxParticipants,
            SourceLanguage = sourceLanguage,
            TargetLanguages = Helpers.LanguageHelper.SerializeTargetLanguages(targetLanguages),
            Settings = request.Settings != null ? System.Text.Json.JsonSerializer.Serialize(new TranslationRoomSettings { RequiresApproval = request.Settings.RequiresApproval, ArtifactAccess = request.Settings.ArtifactAccess }) : "{\"requires_approval\":true,\"artifact_access\":\"HostOnly\"}",
            ScheduledAt = request.ScheduledAt
        };
    }

    public static TranslationRoomDto ToHistoryDto(this TranslationRoom room)
    {
        if (string.IsNullOrEmpty(room.Settings))
        {
            throw new InvalidOperationException($"TranslationRoom {room.Id} is missing Settings configuration in the database.");
        }

        RoomSettingsResponse settings = System.Text.Json.JsonSerializer.Deserialize<RoomSettingsResponse>(
            room.Settings, 
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException($"Failed to deserialize Settings for TranslationRoom {room.Id}");

        var artifacts = room.TranslationRoomArtifacts?.Select(a => a.ToDto()).ToList() ?? new List<RoomArtifactDto>();

        return new TranslationRoomDto(
            room.Id,
            room.WorkspaceId,
            room.HostId,
            room.Title,
            room.Description,
            room.TranslationRoomCode,
            room.Status,
            Enum.Parse<TranslationRoomType>(room.TranslationRoomType, true),
            room.MaxParticipants,
            room.SourceLanguage,
            Helpers.LanguageHelper.ParseTargetLanguages(room.TargetLanguages),
            room.ScheduledAt,
            room.StartedAt,
            room.EndedAt,
            room.CreatedAt,
            settings,
            artifacts
        );
    }
}
