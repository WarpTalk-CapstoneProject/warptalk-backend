namespace WarpTalk.NotificationService.Domain.Entities;

/// <summary>
/// A reusable piece of email design, kept apart from any one email's wording: a LAYOUT (the brand
/// wrapper every email is rendered into) or a PARTIAL (a block such as a signature or a button
/// row, included with <c>{{&gt; key}}</c>).
///
/// Draft and published are separate columns. Senders only ever read the published ones, so an
/// admin can rework a layout for a week without a single email changing until Publish.
/// </summary>
public partial class EmailBlock
{
    public Guid Id { get; set; }

    /// <summary>LAYOUT or PARTIAL.</summary>
    public string Kind { get; set; } = null!;

    /// <summary>Stable slug. Partials are included by it; layouts are chosen by id.</summary>
    public string Key { get; set; } = null!;

    public string Name { get; set; } = null!;

    public string? Description { get; set; }

    /// <summary>ACTIVE or ARCHIVED. An archived block is never used by a send.</summary>
    public string Status { get; set; } = null!;

    /// <summary>LAYOUT only: used by every email that does not choose one.</summary>
    public bool IsDefault { get; set; }

    public string DraftHtml { get; set; } = null!;

    /// <summary>LAYOUT only: the plain-text wrapper, with {{content}}.</summary>
    public string? DraftText { get; set; }

    /// <summary>LAYOUT only: CSS applied in dark mode.</summary>
    public string? DraftDarkCss { get; set; }

    public string? PublishedHtml { get; set; }

    public string? PublishedText { get; set; }

    public string? PublishedDarkCss { get; set; }

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
