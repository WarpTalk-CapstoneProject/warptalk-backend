namespace WarpTalk.NotificationService.Domain.Entities;

/// <summary>
/// One send of a custom email template to an audience, confirmed by an admin or started by an
/// announcement going live. Recipients are resolved when it starts sending and kept, one row each,
/// in email_campaign_recipients with what happened to them.
/// </summary>
public partial class EmailCampaign
{
    public Guid Id { get; set; }

    public string TemplateKey { get; set; } = null!;

    /// <summary>MANUAL or ANNOUNCEMENT.</summary>
    public string Source { get; set; } = null!;

    public Guid? AnnouncementId { get; set; }

    /// <summary>JSON EmailAudienceSpec.</summary>
    public string Audience { get; set; } = null!;

    /// <summary>JSON object of variable name → value, the same for every recipient.</summary>
    public string Values { get; set; } = "{}";

    /// <summary>QUEUED, SENDING, COMPLETED, CANCELLED or FAILED.</summary>
    public string Status { get; set; } = null!;

    public DateTime ScheduledAt { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public int TotalCount { get; set; }

    public int SentCount { get; set; }

    public int FailedCount { get; set; }

    public int SkippedCount { get; set; }

    public string? Error { get; set; }

    public Guid CreatedBy { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CancelledBy { get; set; }

    public DateTime? CancelledAt { get; set; }
}
