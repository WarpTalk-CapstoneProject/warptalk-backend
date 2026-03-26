using System;
using System.Collections.Generic;

namespace WarpTalk.NotificationService.Domain.Entities;

public partial class PushSubscription
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public string DeviceToken { get; set; } = null!;

    public string Platform { get; set; } = null!;

    public string? DeviceName { get; set; }

    public bool IsActive { get; set; }

    public DateTime? LastUsedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
