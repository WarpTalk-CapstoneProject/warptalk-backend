namespace WarpTalk.NotificationService.Domain.Entities;

/// <summary>
/// An image uploaded for an announcement (hero image or inline in the body). Stored in this
/// service's own database: announcement images are small, few, and must be served to every
/// signed-in user by an unguessable URL, which needs no bucket of its own.
/// </summary>
public partial class AnnouncementAsset
{
    public Guid Id { get; set; }

    public string FileName { get; set; } = null!;

    public string ContentType { get; set; } = null!;

    public int SizeBytes { get; set; }

    public byte[] Content { get; set; } = [];

    public Guid CreatedBy { get; set; }

    public DateTime CreatedAt { get; set; }
}
