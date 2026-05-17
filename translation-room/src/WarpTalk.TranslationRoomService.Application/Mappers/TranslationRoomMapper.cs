using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.ValueObjects;

namespace WarpTalk.TranslationRoomService.Application.Mappers;

public static class TranslationRoomMapper
{
    public static TranslationRoomDto ToResponseDto(TranslationRoom room, RoomSettingsResponse settings)
    {
        return new TranslationRoomDto(
            room.Id,
            room.WorkspaceId,
            room.HostId,
            room.Title,
            room.Description,
            room.TranslationRoomCode,
            room.Status.ToString(),
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

    public static TranslationRoom ToEntity(CreateTranslationRoomRequest request, Guid hostId, string roomCode, RoomStatus status, string sourceLanguage, List<string> targetLanguages)
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
            Settings = request.Settings != null ? System.Text.Json.JsonSerializer.Serialize(new TranslationRoomSettings { RequiresApproval = request.Settings.RequiresApproval, HistoryAccess = request.Settings.HistoryAccess }) : "{\"requires_approval\":true,\"history_access\":\"HostOnly\"}",
            ScheduledAt = request.ScheduledAt
        };
    }

    public static TranslationRoomDto ToHistoryDto(TranslationRoom room, RoomSettingsResponse settings, List<RoomArtifactDto> artifacts)
    {
        return new TranslationRoomDto(
            room.Id,
            room.WorkspaceId,
            room.HostId,
            room.Title,
            room.Description,
            room.TranslationRoomCode,
            room.Status.ToString(),
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
