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
            CreatedAt = entity.CreatedAt,
            FileUrl = entity.FileUrl,
            FileName = entity.FileName,
            FileSizeBytes = entity.FileSizeBytes,
            ContentType = entity.ContentType
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

    public static MeetingChatMessage ToFileEntity(
        Guid messageId,
        Guid roomId,
        Guid workspaceId,
        Guid userId,
        MeetingParticipant? participant,
        string fileUrl,
        string fileName,
        long fileSizeBytes,
        string contentType)
    {
        return new MeetingChatMessage
        {
            Id = messageId,
            MeetingRoomId = roomId,
            WorkspaceId = workspaceId,
            SenderUserId = userId,
            ParticipantId = participant?.Id,
            SenderDisplayName = participant?.ProviderIdentity ?? "Unknown User",
            SenderType = "user",
            MessageType = "file",
            OriginalLanguage = "en", // not applicable to file messages; original_language is NOT NULL
            OriginalText = fileName,
            TranslationEnabled = false,
            Mentions = "[]",
            IsHidden = false,
            CreatedAt = DateTime.UtcNow,
            FileUrl = fileUrl,
            FileName = fileName,
            FileSizeBytes = fileSizeBytes,
            ContentType = contentType
        };
    }
}
