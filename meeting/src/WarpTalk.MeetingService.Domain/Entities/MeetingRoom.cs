using System;
using System.Collections.Generic;
using WarpTalk.MeetingService.Domain.Enums;

namespace WarpTalk.MeetingService.Domain.Entities;

public class MeetingRoom
{
    public Guid Id { get; set; }

    public Guid TranslationRoomId { get; set; }

    public string ProviderRoomName { get; set; } = null!;

    public MeetingStatus Status { get; set; }

    public bool IsActive { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTime? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public DateTime? EndedAt { get; set; }

    public virtual ICollection<MeetingParticipant> MeetingParticipants { get; set; } = new List<MeetingParticipant>();
}
