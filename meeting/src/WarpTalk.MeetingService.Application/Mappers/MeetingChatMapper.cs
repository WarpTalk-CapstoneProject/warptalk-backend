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
            ContentType = entity.ContentType,
            SourcesJson = entity.SourcesJson
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
            // WT-356: DisplayName, not ProviderIdentity. ProviderIdentity is the LiveKit identity
            // and MeetingRoomService sets it to the user id, so this column — named for a display
            // name — carried a bare uuid onto every message. It surfaced whenever the frontend's
            // participant lookup missed, which is precisely the case the fallback exists for:
            // somebody who has left the room keeps their name on what they wrote. Null only for
            // participants who joined before display_name existed.
            SenderDisplayName = participant?.DisplayName ?? "Unknown User",
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
            // WT-356: DisplayName, not ProviderIdentity. ProviderIdentity is the LiveKit identity
            // and MeetingRoomService sets it to the user id, so this column — named for a display
            // name — carried a bare uuid onto every message. It surfaced whenever the frontend's
            // participant lookup missed, which is precisely the case the fallback exists for:
            // somebody who has left the room keeps their name on what they wrote. Null only for
            // participants who joined before display_name existed.
            SenderDisplayName = participant?.DisplayName ?? "Unknown User",
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
