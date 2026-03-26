using System;
using System.Collections.Generic;

namespace WarpTalk.NotificationService.Domain.Entities;

public partial class NotificationTemplate
{
    public Guid Id { get; set; }

    public string Type { get; set; } = null!;

    public string Channel { get; set; } = null!;

    public string? Subject { get; set; }

    public string BodyTemplate { get; set; } = null!;

    public string Variables { get; set; } = null!;

    public bool IsActive { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
