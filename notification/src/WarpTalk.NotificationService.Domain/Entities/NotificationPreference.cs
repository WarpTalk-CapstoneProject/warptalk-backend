using System;
using System.Collections.Generic;

namespace WarpTalk.NotificationService.Domain.Entities;

public partial class NotificationPreference
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public string NotificationType { get; set; } = null!;

    public bool EmailEnabled { get; set; }

    public bool PushEnabled { get; set; }

    public bool InAppEnabled { get; set; }

    public DateTime UpdatedAt { get; set; }
}
