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

    /// <summary>
    /// Where it shows: TOP_BANNER, MODAL, TOAST, NOTIFICATION_CENTER or DASHBOARD_CARD. Every value
    /// has a renderer in the web app; a placement nothing draws is not offered.
    /// </summary>
    public string Placement { get; set; } = null!;

    /// <summary>SUBTLE, SOLID or OUTLINE: how loud it is.</summary>
    public string Variant { get; set; } = null!;

    /// <summary>BRAND, BLUE, GREEN, AMBER, RED, VIOLET or NEUTRAL.</summary>
    public string AccentColor { get; set; } = null!;

    /// <summary>A name from the web app's announcement icon set, or null for the type's icon.</summary>
    public string? Icon { get; set; }

    /// <summary>A hero image: an uploaded announcement asset or an https URL.</summary>
    public string? ImageUrl { get; set; }

    /// <summary>Higher shows first. 0–100.</summary>
    public int Priority { get; set; }

    /// <summary>False hides the close button; it shows until its window ends or it is unpublished.</summary>
    public bool Dismissible { get; set; } = true;

    /// <summary>UNTIL_DISMISSED, ONCE, EVERY_SESSION or DAILY.</summary>
    public string Frequency { get; set; } = null!;

    /// <summary>Workspace roles (Owner, Admin, Member…). Empty means any role.</summary>
    public string[] TargetRoles { get; set; } = [];

    /// <summary>UI locales (en, vi, ja). Empty means any locale.</summary>
    public string[] TargetLocales { get; set; } = [];

    /// <summary>Only accounts created within this many days. Null means any account age.</summary>
    public int? NewUsersWithinDays { get; set; }

    public string? CtaLabel { get; set; }

    /// <summary>An absolute http(s) URL or an in-app path starting with "/".</summary>
    public string? CtaUrl { get; set; }

    public string? SecondaryCtaLabel { get; set; }

    public string? SecondaryCtaUrl { get; set; }

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

    /// <summary>
    /// Optional email channel: a custom email template sent to this announcement's audience when it
    /// goes live. Null sends no email.
    /// </summary>
    public string? EmailTemplateKey { get; set; }

    /// <summary>The audience send the email channel created, once it was published.</summary>
    public Guid? EmailCampaignId { get; set; }
}
