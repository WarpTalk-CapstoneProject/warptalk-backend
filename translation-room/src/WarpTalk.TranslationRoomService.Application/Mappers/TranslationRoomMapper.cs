using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.ValueObjects;

namespace WarpTalk.TranslationRoomService.Application.Mappers;

public static class TranslationRoomMapper
{
    public static TranslationRoomDto ToResponseDto(TranslationRoom room)
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
            room.TargetLanguages,
            room.ScheduledAt,
            room.StartedAt,
            room.EndedAt,
            room.CreatedAt,
            settings!
        );
    }

    public static TranslationRoom ToEntity(CreateTranslationRoomRequest request, Guid hostId, string roomCode, RoomStatus status)
    {
        return new TranslationRoom
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = request.WorkspaceId ?? Guid.Empty,
            HostId = hostId,
            Title = request.Title,
            Description = request.Description,
            TranslationRoomCode = roomCode,
            Status = status.ToString(),
            TranslationRoomType = request.TranslationRoomType.ToString(),
            MaxParticipants = request.MaxParticipants,
            SourceLanguage = request.SourceLanguage,
            TargetLanguages = request.TargetLanguages,
            Settings = request.Settings != null ? System.Text.Json.JsonSerializer.Serialize(new TranslationRoomSettings { RequiresApproval = request.Settings.RequiresApproval }) : "{\"requires_approval\":true}",
            ScheduledAt = request.ScheduledAt
        };
    }

    public static TranslationRoomParticipant ToParticipantEntity(Guid translationRoomId, Guid userId, JoinTranslationRoomRequest request, TranslationRoomParticipantStatus initialStatus)
    {
        return new TranslationRoomParticipant
        {
            Id = Guid.CreateVersion7(),
            TranslationRoomId = translationRoomId,
            UserId = userId,
            DisplayName = request.DisplayName,
            Role = TranslationRoomParticipantRole.PARTICIPANT.ToString(),
            ListenLanguage = request.ListenLanguage,
            SpeakLanguage = request.SpeakLanguage,
            Status = initialStatus.ToString(),
            JoinedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    public static TranslationRoomParticipantDto ToParticipantDto(TranslationRoomParticipant participant)
    {
        return new TranslationRoomParticipantDto(
            participant.Id,
            participant.TranslationRoomId,
            participant.UserId,
            participant.DisplayName,
            Enum.Parse<TranslationRoomParticipantRole>(participant.Role, true),
            participant.ListenLanguage,
            participant.SpeakLanguage,
            Enum.Parse<TranslationRoomParticipantStatus>(participant.Status, true),
            participant.JoinedAt
        );
    }
}
