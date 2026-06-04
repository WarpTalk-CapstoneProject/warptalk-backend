using System;
using WarpTalk.MeetingService.Application.DTOs;
using WarpTalk.MeetingService.Domain.Entities;

namespace WarpTalk.MeetingService.Application.Mappers;

public static class MeetingChatMapper
{
    public static MeetingChatMessageDto ToDto(this MeetingChatMessage entity)
    {
        return new MeetingChatMessageDto
        {
            Id = entity.Id,
            MeetingRoomId = entity.MeetingRoomId,
            SenderUserId = entity.SenderUserId,
            SenderDisplayName = entity.SenderDisplayName,
            SenderType = entity.SenderType,
            MessageType = entity.MessageType,
            OriginalLanguage = entity.OriginalLanguage,
            OriginalText = entity.OriginalText,
            TranslationEnabled = entity.TranslationEnabled,
            ContainsWarpbotMention = entity.ContainsWarpbotMention,
            CreatedAt = entity.CreatedAt
        };
    }

    public static MeetingChatMessage ToEntity(
        this SendMeetingChatMessageRequest request, 
        Guid roomId, 
        Guid workspaceId, 
        Guid userId, 
        MeetingParticipant? participant)
    {
        return new MeetingChatMessage
        {
            Id = Guid.NewGuid(),
            MeetingRoomId = roomId,
            WorkspaceId = workspaceId,
            SenderUserId = userId,
            ParticipantId = participant?.Id,
            SenderDisplayName = participant?.ProviderIdentity ?? "Unknown User",
            SenderType = "user",
            MessageType = request.MessageType,
            OriginalLanguage = request.OriginalLanguage,
            OriginalText = request.OriginalText,
            TranslationEnabled = request.TranslationEnabled,
            ContainsWarpbotMention = request.ContainsWarpbotMention,
            IsHidden = false,
            CreatedAt = DateTime.UtcNow
        };
    }
}
