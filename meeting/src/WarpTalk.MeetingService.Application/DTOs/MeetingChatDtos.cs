using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.AspNetCore.Http;

namespace WarpTalk.MeetingService.Application.DTOs;

public class ChatMentionDto
{
    public string Id { get; set; } = null!;
    public string Display { get; set; } = null!;
    public string Type { get; set; } = null!; // "agent" or "user"
}

public class MeetingChatMessageDto
{
    public Guid Id { get; set; }
    public Guid MeetingRoomId { get; set; }
    public Guid? SenderUserId { get; set; }
    public string SenderDisplayName { get; set; } = null!;
    public string SenderType { get; set; } = null!;
    public string MessageType { get; set; } = null!;
    public string OriginalLanguage { get; set; } = null!;
    public string OriginalText { get; set; } = null!;
    public bool TranslationEnabled { get; set; }
    public List<ChatMentionDto> Mentions { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public string? FileUrl { get; set; }
    public string? FileName { get; set; }
    public long? FileSizeBytes { get; set; }
    public string? ContentType { get; set; }
}

public class SendMeetingChatMessageRequest
{
    public string OriginalText { get; set; } = null!;
    public string OriginalLanguage { get; set; } = null!;
    public bool TranslationEnabled { get; set; }
    public List<ChatMentionDto> Mentions { get; set; } = new();
    public string MessageType { get; set; } = "text";
}

public class TranslateMeetingChatMessageRequest
{
    public string TargetLanguage { get; set; } = null!;
}

public class MeetingChatTranslationDto
{
    public Guid MessageId { get; set; }
    public string TargetLanguage { get; set; } = null!;
    public string TranslatedText { get; set; } = null!;
    public bool Cached { get; set; }
}

public class ModerateMeetingChatMessageRequest
{
    public string Reason { get; set; } = null!;
}

public class UploadMeetingChatFileRequest
{
    public IFormFile File { get; set; } = null!;
}

public class MeetingChatFileDownloadResult
{
    public Stream Stream { get; set; } = null!;
    public string ContentType { get; set; } = null!;
    public string FileName { get; set; } = null!;
}
