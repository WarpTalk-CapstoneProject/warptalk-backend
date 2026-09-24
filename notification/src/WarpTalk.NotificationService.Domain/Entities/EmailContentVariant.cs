namespace WarpTalk.NotificationService.Domain.Entities;

/// <summary>
/// One transactional email's content in one language: subject, preheader, heading, HTML body,
/// optional hand-written plain text, and the layout it is rendered into.
///
/// The emails themselves are the backend's EmailTemplateCatalog — a row here can only exist for an
/// email a sender composes. A missing row (or an archived one) means the sender uses the next
/// locale in the fallback chain, and finally the built-in wording.
/// </summary>
public partial class EmailContentVariant
{
    public Guid Id { get; set; }

    /// <summary>An EmailTemplateCatalog key, e.g. "auth.verify-email".</summary>
    public string TemplateKey { get; set; } = null!;

    /// <summary>en, vi or ja.</summary>
    public string Locale { get; set; } = null!;

    /// <summary>ACTIVE or ARCHIVED.</summary>
    public string Status { get; set; } = null!;

    public string DraftSubject { get; set; } = null!;

    public string DraftPreheader { get; set; } = null!;

    public string DraftHeading { get; set; } = null!;

    public string DraftBodyHtml { get; set; } = null!;

    /// <summary>Null derives the plain text from the HTML.</summary>
    public string? DraftTextBody { get; set; }

    /// <summary>Null uses the default layout.</summary>
    public Guid? DraftLayoutId { get; set; }

    public string? PublishedSubject { get; set; }

    public string? PublishedPreheader { get; set; }

    public string? PublishedHeading { get; set; }

    public string? PublishedBodyHtml { get; set; }

    public string? PublishedTextBody { get; set; }

    public Guid? PublishedLayoutId { get; set; }

    /// <summary>0 until first published; +1 on every publish.</summary>
    public int PublishedVersion { get; set; }

    public DateTime? PublishedAt { get; set; }

    public Guid? PublishedBy { get; set; }

    public DateTime DraftUpdatedAt { get; set; }

    public Guid DraftUpdatedBy { get; set; }

    public DateTime? ArchivedAt { get; set; }

    public Guid CreatedBy { get; set; }

    public DateTime CreatedAt { get; set; }
}
