using System;
using System.Collections.Generic;
using System.Text.Json;
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
            Mentions = string.IsNullOrEmpty(entity.Mentions) ? new List<ChatMentionDto>() : JsonSerializer.Deserialize<List<ChatMentionDto>>(entity.Mentions, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<ChatMentionDto>(),
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
            Mentions = request.Mentions == null ? "[]" : JsonSerializer.Serialize(request.Mentions, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
            IsHidden = false,
            CreatedAt = DateTime.UtcNow
        };
    }
}
