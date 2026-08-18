using System;
using System.Collections.Generic;

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

    public string Status { get; set; } = null!;

    public string ConnectionType { get; set; } = null!;

    public bool IsTranslationAudioEnabled { get; set; }

    public bool IsUsingVoiceClone { get; set; }

    /// <summary>
    /// WT-446: this participant was not an active member of the room's workspace when they joined.
    /// Resolved once, on join, through IWorkspaceMemberDirectory — never recomputed on read, because
    /// the roster is polled every few seconds and externality is a fact about admission, not a live
    /// property. Defaults to false, which is both the common case and the safe answer for the rows
    /// that predate the column.
    /// </summary>
    public bool IsExternal { get; set; }

    public DateTime? JoinedAt { get; set; }

    public DateTime? LeftAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public virtual TranslationRoom TranslationRoom { get; set; } = null!;

    public virtual ICollection<TranslationRoomAudioRoute> TranslationRoomAudioRouteSourceParticipants { get; set; } = new List<TranslationRoomAudioRoute>();

    public virtual ICollection<TranslationRoomAudioRoute> TranslationRoomAudioRouteTargetParticipants { get; set; } = new List<TranslationRoomAudioRoute>();
}
