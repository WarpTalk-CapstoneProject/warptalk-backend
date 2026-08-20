using System;
using System.Collections.Generic;

namespace WarpTalk.MeetingService.Domain.Entities;

public partial class MeetingChatMessage
{
    public Guid Id { get; set; }

    public Guid MeetingRoomId { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid? SenderUserId { get; set; }

    public Guid? ParticipantId { get; set; }

    public string SenderDisplayName { get; set; } = null!;

    public string SenderType { get; set; } = null!;

    public string MessageType { get; set; } = null!;

    public string OriginalLanguage { get; set; } = null!;

    public string OriginalText { get; set; } = null!;

    public bool TranslationEnabled { get; set; }

    public bool IsHidden { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public string Mentions { get; set; } = null!;

    /// <summary>Only populated when MessageType == "file".</summary>
    public string? FileUrl { get; set; }

    public string? FileName { get; set; }

    public long? FileSizeBytes { get; set; }

    public string? ContentType { get; set; }

    /// <summary>
    /// Sources a WarpBot answer cited, as the stored JSON array. NULL on everything a person
    /// wrote — provenance is a claim only an answer makes.
    /// </summary>
    public string? SourcesJson { get; set; }

    public virtual ICollection<MeetingChatAssistantRequest> MeetingChatAssistantRequests { get; set; } = new List<MeetingChatAssistantRequest>();

    public virtual ICollection<MeetingChatModerationEvent> MeetingChatModerationEvents { get; set; } = new List<MeetingChatModerationEvent>();

    public virtual ICollection<MeetingChatTranslation> MeetingChatTranslations { get; set; } = new List<MeetingChatTranslation>();

    public virtual MeetingRoom MeetingRoom { get; set; } = null!;

    public virtual MeetingParticipant? Participant { get; set; }
}
