namespace WarpTalk.NotificationService.Domain.Entities;

public sealed class NotificationInboxMessage
{
    public Guid EventId { get; set; }
    public string Consumer { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public DateTime ProcessedAt { get; set; }
}
