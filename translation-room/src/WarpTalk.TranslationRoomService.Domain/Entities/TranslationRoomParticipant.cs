using System;
using System.Collections.Generic;
using WarpTalk.TranslationRoomService.Domain.Enums;

namespace WarpTalk.TranslationRoomService.Domain.Entities;

/// <summary>
/// Participant lifecycle:
/// INVITED -&gt; WAITING
/// WAITING -&gt; CONNECTED
/// WAITING -&gt; REJECTED
/// CONNECTED -&gt; DISCONNECTED
/// DISCONNECTED -&gt; CONNECTED
/// CONNECTED -&gt; LEFT
/// CONNECTED -&gt; KICKED
/// 
/// MUTED is not a participant_status. It is represented by is_muted.
/// 
/// </summary>
public partial class TranslationRoomParticipant
{
    public Guid Id { get; set; }

    public Guid TranslationRoomId { get; set; }

    /// <summary>
    /// External AuthService user id. Nullable for guests. No physical FK.
    /// </summary>
    public Guid? UserId { get; set; }

    public string DisplayName { get; set; } = null!;

    public string Role { get; set; } = null!;

    public string ListenLanguage { get; set; } = null!;

    public string SpeakLanguage { get; set; } = null!;

    public string ConnectionType { get; set; } = null!;

    public TranslationRoomParticipantStatus Status { get; set; }

    public bool IsTranslationAudioEnabled { get; set; }

    public bool IsUsingVoiceClone { get; set; }

    public DateTime? JoinedAt { get; set; }

    public DateTime? LeftAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public virtual TranslationRoom TranslationRoom { get; set; } = null!;

    public virtual ICollection<TranslationRoomAudioRoute> TranslationRoomAudioRouteSourceParticipants { get; set; } = new List<TranslationRoomAudioRoute>();

    public virtual ICollection<TranslationRoomAudioRoute> TranslationRoomAudioRouteTargetParticipants { get; set; } = new List<TranslationRoomAudioRoute>();
}
