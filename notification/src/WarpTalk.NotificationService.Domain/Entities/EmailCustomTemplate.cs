namespace WarpTalk.NotificationService.Domain.Entities;

/// <summary>
/// An email template an admin created, as opposed to one a service sends from code
/// (EmailTemplateCatalog). Its content lives in email_content_variants under the same key, so
/// drafts, publishing and versions work exactly as they do for a built-in. It has no code sender:
/// it goes out through an audience send (EmailCampaign) or an announcement's email channel.
/// </summary>
public partial class EmailCustomTemplate
{
    public Guid Id { get; set; }

    public string Key { get; set; } = null!;

    public string Name { get; set; } = null!;

    public string? Description { get; set; }

    /// <summary>TRANSACTIONAL_CUSTOM, MARKETING or ANNOUNCEMENT.</summary>
    public string Category { get; set; } = null!;

    /// <summary>JSON array of EmailCustomVariable.</summary>
    public string Variables { get; set; } = "[]";

    /// <summary>ACTIVE or DELETED (soft).</summary>
    public string Status { get; set; } = null!;

    public DateTime? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public string? DeleteReason { get; set; }

    public Guid CreatedBy { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTime UpdatedAt { get; set; }
}
