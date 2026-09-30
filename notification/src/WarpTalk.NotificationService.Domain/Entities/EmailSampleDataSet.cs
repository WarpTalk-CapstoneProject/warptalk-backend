namespace WarpTalk.NotificationService.Domain.Entities;

/// <summary>
/// Named values for one email's variables — "long workspace name", "Vietnamese invitee" — used by
/// the preview and the test email so an edit can be checked against more than one case.
/// </summary>
public partial class EmailSampleDataSet
{
    public Guid Id { get; set; }

    public string TemplateKey { get; set; } = null!;

    public string Name { get; set; } = null!;

    /// <summary>JSON object of variable name → value.</summary>
    public string Values { get; set; } = null!;

    public Guid CreatedBy { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTime UpdatedAt { get; set; }
}
