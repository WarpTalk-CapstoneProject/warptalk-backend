using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.ValueObjects;

namespace WarpTalk.TranslationRoomService.Application.Mappers;

public static class TranslationRoomMapper
{
    public static TranslationRoomDto ToResponseDto(this TranslationRoom room)
    {
        var settings = !string.IsNullOrEmpty(room.Settings) 
            ? System.Text.Json.JsonSerializer.Deserialize<RoomSettingsResponse>(room.Settings) 
            : new RoomSettingsResponse(true);

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
            room.DurationSeconds,
            room.CreatedAt,
            settings!
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
            Settings = request.Settings != null ? System.Text.Json.JsonSerializer.Serialize(new TranslationRoomSettings { RequiresApproval = request.Settings.RequiresApproval }) : "{\"requires_approval\":true}",
            ScheduledAt = request.ScheduledAt,
            IsActive = true
        };
    }
}
