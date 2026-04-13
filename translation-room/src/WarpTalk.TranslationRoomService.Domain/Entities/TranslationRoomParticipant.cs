using System;
using System.Collections.Generic;

namespace WarpTalk.TranslationRoomService.Infrastructure;

public partial class TranslationRoomParticipant
{
    public Guid Id { get; set; }

    public Guid TranslationRoomId { get; set; }

    public Guid UserId { get; set; }

    public string DisplayName { get; set; } = null!;

    public string Role { get; set; } = null!;

    public string ListenLanguage { get; set; } = null!;

    public string SpeakLanguage { get; set; } = null!;

    public string Status { get; set; } = null!;

    public string? ConnectionType { get; set; }

    public bool IsMuted { get; set; }

    public bool IsUsingVoiceClone { get; set; }

    public DateTime? JoinedAt { get; set; }

    public DateTime? LeftAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public virtual TranslationRoom TranslationRoom { get; set; } = null!;

    public virtual ICollection<TranslationRoomAudioRoute> TranslationRoomAudioRouteSourceParticipants { get; set; } = new List<TranslationRoomAudioRoute>();

    public virtual ICollection<TranslationRoomAudioRoute> TranslationRoomAudioRouteTargetParticipants { get; set; } = new List<TranslationRoomAudioRoute>();
}
