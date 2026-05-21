using System;
using System.Collections.Generic;

namespace WarpTalk.TranslationRoomService.Domain.Entities;

public partial class TranslationRoomAudioRoute
{
    public Guid Id { get; set; }

    public Guid TranslationRoomId { get; set; }

    public Guid SourceParticipantId { get; set; }

    public Guid TargetParticipantId { get; set; }

    public string SourceLanguage { get; set; } = null!;

    public string TargetLanguage { get; set; } = null!;

    public bool VoiceCloneEnabled { get; set; }

    public string? StreamId { get; set; }

    public string Status { get; set; } = null!;

    public DateTime StartedAt { get; set; }

    public DateTime? EndedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public virtual TranslationRoomParticipant SourceParticipant { get; set; } = null!;

    public virtual TranslationRoomParticipant TargetParticipant { get; set; } = null!;

    public virtual TranslationRoom TranslationRoom { get; set; } = null!;
}
