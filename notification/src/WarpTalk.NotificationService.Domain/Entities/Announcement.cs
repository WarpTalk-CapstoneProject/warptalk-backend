namespace WarpTalk.NotificationService.Domain.Entities;

/// <summary>
/// A piece of platform news shown inside the app — a feature launch, a maintenance window, a
/// promotion — managed like CMS content: drafted, previewed, published or scheduled, and archived.
///
/// Distinct from <see cref="AdminNotification"/>, which is a one-shot delivery into people's
/// notification inboxes. An announcement is not delivered to anyone; it is shown to whoever is in
/// its audience while its window is open, and stops being shown when the window closes or an
/// admin unpublishes it.
/// </summary>
public partial class Announcement
{
    public Guid Id { get; set; }

    public string Title { get; set; } = null!;

    /// <summary>Markdown. Rendered by the web client without raw HTML.</summary>
    public string BodyMarkdown { get; set; } = null!;

    /// <summary>ANNOUNCEMENT, FEATURE, MAINTENANCE or PROMOTION.</summary>
    public string Type { get; set; } = null!;

    /// <summary>
    /// DRAFT, PUBLISHED or ARCHIVED. "Scheduled" and "ended" are not stored: they are a published
    /// announcement whose window has not opened yet, or has closed — see AnnouncementLifecycle.
    /// </summary>
    public string Status { get; set; } = null!;

    /// <summary>ALL, PLANS or WORKSPACES.</summary>
    public string AudienceMode { get; set; } = null!;

    /// <summary>PLANS: the plan slugs whose workspaces' members see it.</summary>
    public string[] AudiencePlanSlugs { get; set; } = [];

    /// <summary>WORKSPACES: the workspaces whose members see it.</summary>
    public Guid[] AudienceWorkspaceIds { get; set; } = [];

    public string? CtaLabel { get; set; }

    /// <summary>An absolute http(s) URL or an in-app path starting with "/".</summary>
    public string? CtaUrl { get; set; }

    /// <summary>When it starts showing. Null means from the moment it is published.</summary>
    public DateTime? StartsAt { get; set; }

    /// <summary>When it stops showing. Null means until unpublished or archived.</summary>
    public DateTime? EndsAt { get; set; }

    /// <summary>The last time it was published (now, or as a schedule).</summary>
    public DateTime? PublishedAt { get; set; }

    public Guid? PublishedBy { get; set; }

    public DateTime? ArchivedAt { get; set; }

    public Guid CreatedBy { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
