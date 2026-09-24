using System;
using System.Collections.Generic;

namespace WarpTalk.AssistantService.Domain.Entities;

/// <summary>
/// A system administrator's WarpBot conversation in the admin portal — scope "platform".
///
/// Deliberately its own table rather than an <see cref="AssistantConversation"/> with a flag.
/// Every workspace conversation carries a workspace id that its tools, history and retrieval are
/// scoped by; a platform conversation has none, and keeping the two stores apart means no query
/// written for one scope can ever return a row of the other. There is no workspace column here to
/// get wrong: the table cannot hold workspace data.
/// </summary>
public partial class PlatformConversation
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public string Title { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public DateTime? LastMessageAt { get; set; }

    public bool IsArchived { get; set; }

    public virtual ICollection<PlatformMessage> Messages { get; set; } = new List<PlatformMessage>();
}
