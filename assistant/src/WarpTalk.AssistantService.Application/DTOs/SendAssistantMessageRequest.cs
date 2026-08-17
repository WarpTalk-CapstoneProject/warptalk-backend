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

/// <summary>
/// WT-474: one attachment on a chat message.
///
/// DataUrl carries the bytes as `data:&lt;mime&gt;;base64,...`. Name is kept because the Responses API
/// requires a filename for a document part, and because it is the only handle the model has for
/// referring to one document among several — "the contract" is not resolvable from bytes alone.
///
/// MimeType is ADVISORY. The worker reads the real type off the data URL, since the two can
/// disagree and the bytes are the only side that decides how OpenAI reads them.
/// </summary>
public record AssistantAttachmentDto(
    [Required] string DataUrl,
    string? Name = null,
    string? MimeType = null
);

public record SendAssistantMessageRequest(
    [Required] string Content,
    AssistantPageContextDto? PageContext = null,
    List<AssistantMentionDto>? Mentions = null,
    /// <summary>
    /// WT-474: files pasted, dropped or picked in the chat box — images AND documents.
    ///
    /// They belong to THIS TURN. Nothing stores them: they are forwarded to the worker with the
    /// request and are not written to AssistantMessage.Content, so a follow-up question cannot see
    /// them. That is deliberate — a file kept against a conversation becomes a new kind of
    /// workspace content, and every kind of workspace content has to answer to the visibility model
    /// WT-463 is still defining.
    ///
    /// Validated, not trusted. SerializeAttachments caps the count and the size and accepts only
    /// the types the worker can actually submit, because this field is the one part of the request
    /// that can be megabytes long.
    /// </summary>
    List<AssistantAttachmentDto>? Attachments = null
);

public record SendAssistantMessageResponse(
    Guid MessageId,
    Guid AssistantMessageId
);

public record CreateAssistantConversationRequest(
    [Required] Guid WorkspaceId
);
