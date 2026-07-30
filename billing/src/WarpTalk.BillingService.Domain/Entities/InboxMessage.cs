namespace WarpTalk.BillingService.Domain.Entities;

public sealed class InboxMessage
{
    public Guid EventId { get; set; }
    public string Consumer { get; set; } = string.Empty;
    public DateTime ProcessedAt { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string? LastError { get; set; }
}
