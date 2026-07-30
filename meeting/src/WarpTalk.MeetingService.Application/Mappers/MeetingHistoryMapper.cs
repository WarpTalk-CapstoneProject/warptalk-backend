using System;
using System.Collections.Generic;
using System.Linq;
using WarpTalk.MeetingService.Application.DTOs;
using WarpTalk.MeetingService.Domain.Entities;

namespace WarpTalk.MeetingService.Application.Mappers;

public static class MeetingHistoryMapper
{
    public static MeetingRoomDto ToRoomDto(this MeetingRoom room, Guid userId, int participantCount = 0, int chatMessageCount = 0)
    {
        return new MeetingRoomDto
        {
            Id = room.Id,
            TranslationRoomId = room.TranslationRoomId,
            ProviderRoomName = room.ProviderRoomName,
            Status = room.Status.ToString(),
            IsActive = room.IsActive,
            CreatedBy = room.CreatedBy,
            CreatedAt = room.CreatedAt,
            EndedAt = room.EndedAt,
            DurationSeconds = room.EndedAt.HasValue && room.CreatedAt != default
                ? (int)(room.EndedAt.Value - room.CreatedAt).TotalSeconds
                : null,
            ParticipantCount = participantCount,
            ChatMessageCount = chatMessageCount,
            IsHost = room.CreatedBy == userId
        };
    }

    public static MeetingParticipantDto ToParticipantDto(this MeetingParticipant participant)
    {
        return new MeetingParticipantDto
        {
            Id = participant.Id,
            MeetingRoomId = participant.MeetingRoomId,
            UserId = participant.UserId,
            ProviderIdentity = participant.ProviderIdentity,
            IsActive = participant.IsActive,
            JoinedAt = participant.JoinedAt,
            LeftAt = participant.LeftAt
        };
    }
}
