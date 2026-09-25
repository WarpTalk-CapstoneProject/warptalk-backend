namespace WarpTalk.NotificationService.Domain.Entities;

/// <summary>
/// One person's history with one announcement: when they saw it, how often, whether they closed
/// it, and whether they clicked it. It is what the show-frequency rules read and what the
/// unique-viewer and click-through analytics count.
/// </summary>
public partial class AnnouncementViewerState
{
    public Guid AnnouncementId { get; set; }

    public Guid UserId { get; set; }

    public int ImpressionCount { get; set; }

    public DateTime? FirstSeenAt { get; set; }

    public DateTime? LastSeenAt { get; set; }

    /// <summary>The browser session of the last impression (a random id the web app keeps per tab session).</summary>
    public string? LastSessionId { get; set; }

    public DateTime? DismissedAt { get; set; }

    /// <summary>The session it was dismissed in. EVERY_SESSION announcements come back in the next one.</summary>
    public string? DismissedSessionId { get; set; }

    public int CtaClickCount { get; set; }

    public int SecondaryClickCount { get; set; }

    public DateTime? LastClickedAt { get; set; }
}
