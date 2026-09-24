namespace WarpTalk.NotificationService.Domain.Entities;

/// <summary>Someone closed an announcement; it is not shown to them again.</summary>
public partial class AnnouncementDismissal
{
    public Guid AnnouncementId { get; set; }

    public Guid UserId { get; set; }

    public DateTime DismissedAt { get; set; }
}
