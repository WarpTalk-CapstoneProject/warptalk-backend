using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace WarpTalk.AssistantService.Application.DTOs;

/// <summary>
/// Ambient "what page is the user looking at" hint sent from the frontend's Ask WarpTalk
/// widget. Snapshot must stay a thin, display-only projection (id/title/status) — never
/// raw sensitive data; anything the assistant needs beyond that is fetched by a tool
/// using the caller's own bearer token, not from this snapshot.
/// </summary>
public record AssistantPageContextDto(
    [Required] string PageType,
    string? EntityId,
    string? WorkspaceId,
    Dictionary<string, string>? Snapshot
);

/// <summary>
/// An explicit "@mention" the user attached to this message (a room, document, or member
/// picked from the widget's @ menu) — as opposed to AssistantPageContextDto's ambient,
/// automatic page context. EntityId is scoped to the conversation's own workspace
/// server-side (see AssistantConversationService.SerializeMentions); this DTO carries no
/// workspace id of its own so a client can't smuggle one in from elsewhere.
/// </summary>
public record AssistantMentionDto(
    [Required] string EntityType,
    [Required] string EntityId,
    string? Label
);

public record SendAssistantMessageRequest(
    [Required] string Content,
    AssistantPageContextDto? PageContext = null,
    List<AssistantMentionDto>? Mentions = null
);

public record SendAssistantMessageResponse(
    Guid MessageId,
    Guid AssistantMessageId
);

public record CreateAssistantConversationRequest(
    [Required] Guid WorkspaceId
);
