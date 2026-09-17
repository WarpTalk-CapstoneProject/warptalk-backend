using System;
using System.Collections.Generic;

namespace WarpTalk.NotificationService.Domain.Entities;

public partial class AdminNotification
{
    public Guid Id { get; set; }

    public string Title { get; set; } = null!;

    public string Content { get; set; } = null!;

    public string Type { get; set; } = null!;

    public string Payload { get; set; } = null!;

    public string TargetAudienceMode { get; set; } = null!;

    public string TargetAudienceData { get; set; } = null!;

    public string Status { get; set; } = null!;

    /// <summary>
    /// When the last delivery chunk was written. Null until <see cref="Status"/> becomes "Sent".
    /// </summary>
    public DateTime? SentAt { get; set; }

    /// <summary>Recipients who have a notification row for this announcement so far.</summary>
    public int DeliveredCount { get; set; }

    /// <summary>
    /// How many delivery events were published for this announcement (one per 1,000 recipients).
    /// "Sent" means every one of them was processed, not merely the first.
    /// </summary>
    public int DeliveryChunkCount { get; set; } = 1;

    /// <summary>Delivery events processed so far. Only ever advanced by an atomic UPDATE.</summary>
    public int DeliveredChunkCount { get; set; }

    public Guid CreatedBy { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
