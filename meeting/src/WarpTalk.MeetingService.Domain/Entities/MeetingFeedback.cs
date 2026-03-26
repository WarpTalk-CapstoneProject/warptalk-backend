using System;
using System.Collections.Generic;

namespace WarpTalk.MeetingService.Infrastructure;

public partial class MeetingFeedback
{
    public Guid Id { get; set; }

    public Guid MeetingId { get; set; }

    public Guid UserId { get; set; }

    public int OverallRating { get; set; }

    public int? TranslationQuality { get; set; }

    public int? AudioQuality { get; set; }

    public int? VoiceCloneQuality { get; set; }

    public string? Comments { get; set; }

    public string? CommunicationInsights { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual Meeting Meeting { get; set; } = null!;
}
