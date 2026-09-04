using System;
using System.Collections.Generic;

namespace WarpTalk.MeetingService.Domain.Entities;

public partial class MeetingTrack
{
    public Guid Id { get; set; }

    public Guid RtcStreamParticipantId { get; set; }

    public string ProviderTrackId { get; set; } = null!;

    public string MediaType { get; set; } = null!;

    public bool IsMuted { get; set; }

    public bool IsActive { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTime? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public DateTime? PublishedAt { get; set; }

    public DateTime? UnpublishedAt { get; set; }

    public virtual RtcStreamParticipant RtcStreamParticipant { get; set; } = null!;
}
