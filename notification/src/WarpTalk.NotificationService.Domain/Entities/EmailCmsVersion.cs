namespace WarpTalk.NotificationService.Domain.Entities;

/// <summary>
/// A published version of email content or of a block, frozen as it went out. Append-only: a
/// restore copies a version back into the draft, it never rewrites history.
/// </summary>
public partial class EmailCmsVersion
{
    public Guid Id { get; set; }

    /// <summary>CONTENT (an EmailContentVariant) or BLOCK (an EmailBlock).</summary>
    public string OwnerType { get; set; } = null!;

    public Guid OwnerId { get; set; }

    public int Version { get; set; }

    /// <summary>PUBLISHED.</summary>
    public string Action { get; set; } = null!;

    /// <summary>JSON: the published fields at that version.</summary>
    public string Snapshot { get; set; } = null!;

    public string? Note { get; set; }

    public Guid CreatedBy { get; set; }

    public DateTime CreatedAt { get; set; }
}
