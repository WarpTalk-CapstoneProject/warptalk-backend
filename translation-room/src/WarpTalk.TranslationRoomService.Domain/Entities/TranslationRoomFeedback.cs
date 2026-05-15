using System;
using System.Collections.Generic;

namespace WarpTalk.TranslationRoomService.Domain.Entities;

public partial class TranslationRoomFeedback
{
    public Guid Id { get; set; }

    public Guid TranslationRoomId { get; set; }

    /// <summary>
    /// External AuthService user id. No physical FK.
    /// </summary>
    public Guid UserId { get; set; }

    public int OverallRating { get; set; }

    public int? TranslationQuality { get; set; }

    public int? AudioQuality { get; set; }

    public int? VoiceCloneQuality { get; set; }

    public int? AiSummaryQuality { get; set; }

    public string? Comments { get; set; }

    public string? CommunicationInsights { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual TranslationRoom TranslationRoom { get; set; } = null!;
}
