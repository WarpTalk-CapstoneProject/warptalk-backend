namespace WarpTalk.NotificationService.Domain.Entities;

/// <summary>One person an audience send resolved to, and what happened to their email.</summary>
public partial class EmailCampaignRecipient
{
    public Guid Id { get; set; }

    public Guid CampaignId { get; set; }

    public Guid UserId { get; set; }

    public string Email { get; set; } = null!;

    public string? FullName { get; set; }

    /// <summary>The recipient's language (en, vi or ja) — which variant they are sent.</summary>
    public string Locale { get; set; } = null!;

    /// <summary>PENDING, SENT, FAILED or SKIPPED.</summary>
    public string Status { get; set; } = null!;

    public string? Error { get; set; }

    public DateTime? SentAt { get; set; }
}
