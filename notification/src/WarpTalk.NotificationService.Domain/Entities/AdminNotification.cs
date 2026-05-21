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

    public Guid CreatedBy { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
