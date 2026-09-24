namespace WarpTalk.NotificationService.Domain.Entities;

/// <summary>
/// One entry in an email template's history: what it said after a save, a restore or a reset,
/// and who did it. Append-only — restoring a version writes a new entry rather than rewinding.
/// </summary>
public partial class NotificationTemplateVersion
{
    public Guid Id { get; set; }

    /// <summary>The template key, e.g. "auth.verify-email" (notification_templates.type).</summary>
    public string TemplateType { get; set; } = null!;

    public string Channel { get; set; } = null!;

    public int Version { get; set; }

    /// <summary>SAVED, RESTORED or RESET.</summary>
    public string Action { get; set; } = null!;

    /// <summary>For RESTORED: the version whose content was brought back.</summary>
    public int? RestoredFromVersion { get; set; }

    public string Subject { get; set; } = null!;

    public string Heading { get; set; } = null!;

    public string BodyTemplate { get; set; } = null!;

    public string? Note { get; set; }

    public Guid CreatedBy { get; set; }

    public DateTime CreatedAt { get; set; }
}
