using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Enums;

namespace WarpTalk.TranslationRoomService.Application.Mappers;

public static class TranslationRoomMapper
{
    public static TranslationRoomDto ToResponseDto(TranslationRoom room)
    {
        return new TranslationRoomDto(
            room.Id,
            room.WorkspaceId,
            room.HostId,
            room.Title,
            room.Description,
            room.TranslationRoomCode,
            room.Status.ToString(),
            room.TranslationRoomType,
            room.MaxParticipants,
            room.ScheduledAt,
            room.StartedAt,
            room.EndedAt,
            room.CreatedAt
        );
    }

    public static TranslationRoom ToEntity(CreateTranslationRoomRequest request, Guid hostId, string roomCode, RoomStatus status)
    {
        return new TranslationRoom
        {
            WorkspaceId = request.WorkspaceId ?? Guid.Empty,
            HostId = hostId,
            Title = request.Title,
            Description = request.Description,
            TranslationRoomCode = roomCode,
            Status = status,
            TranslationRoomType = request.TranslationRoomType,
            MaxParticipants = request.MaxParticipants,
            SourceLanguage = request.SourceLanguage,
            TargetLanguages = request.TargetLanguages,
            ScheduledAt = request.ScheduledAt
        };
    }

    public static TranslationRoomParticipant ToParticipantEntity(Guid translationRoomId, Guid userId, JoinTranslationRoomRequest request)
    {
        return new TranslationRoomParticipant
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = translationRoomId,
            UserId = userId,
            DisplayName = request.DisplayName,
            Role = TranslationRoomConstants.Roles.Participant,
            ListenLanguage = request.ListenLanguage,
            SpeakLanguage = request.SpeakLanguage,
            Status = TranslationRoomConstants.ParticipantStatus.Connected,
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
            participant.Role,
            participant.ListenLanguage,
            participant.SpeakLanguage,
            participant.Status,
            participant.JoinedAt
        );
    }
}
