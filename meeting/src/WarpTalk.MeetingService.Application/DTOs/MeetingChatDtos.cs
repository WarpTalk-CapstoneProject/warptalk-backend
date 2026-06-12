using System;

namespace WarpTalk.MeetingService.Application.DTOs;

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
    public bool ContainsWarpbotMention { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class SendMeetingChatMessageRequest
{
    public string OriginalText { get; set; } = null!;
    public string OriginalLanguage { get; set; } = null!;
    public bool TranslationEnabled { get; set; }
    public bool ContainsWarpbotMention { get; set; }
    public string MessageType { get; set; } = "text";
}

public class TranslateMeetingChatMessageRequest
{
    public string TargetLanguage { get; set; } = null!;
}

public class ModerateMeetingChatMessageRequest
{
    public string Reason { get; set; } = null!;
}
