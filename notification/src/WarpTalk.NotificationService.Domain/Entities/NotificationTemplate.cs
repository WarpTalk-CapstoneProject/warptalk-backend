using System;
using System.Collections.Generic;

namespace WarpTalk.NotificationService.Domain.Entities;

public partial class NotificationTemplate
{
    public Guid Id { get; set; }

    public string Type { get; set; } = null!;

    public string Channel { get; set; } = null!;

    public string? Subject { get; set; }

    /// <summary>
    /// The email's title line (EMAIL channel). Added with the email template CMS; null on rows
    /// written before it.
    /// </summary>
    public string? Heading { get; set; }

    public string BodyTemplate { get; set; } = null!;

    public string Variables { get; set; } = null!;

    /// <summary>
    /// False once an admin resets the email to its built-in wording. The row is kept so the
    /// version counter, and the history it numbers, carry on from where they were.
    /// </summary>
    public bool IsActive { get; set; }

    /// <summary>Increases on every save, restore and reset. Matches the newest history row.</summary>
    public int Version { get; set; }

    public Guid? CreatedBy { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
