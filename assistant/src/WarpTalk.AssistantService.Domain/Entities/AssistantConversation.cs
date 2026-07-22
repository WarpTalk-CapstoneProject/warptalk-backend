using System;
using System.Collections.Generic;

namespace WarpTalk.AssistantService.Domain.Entities;

public partial class AssistantConversation
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid UserId { get; set; }

    public string Title { get; set; } = null!;

    public string? ContextScope { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? LastMessageAt { get; set; }

    public bool IsArchived { get; set; }

    public virtual ICollection<AssistantMessage> Messages { get; set; } = new List<AssistantMessage>();
}
