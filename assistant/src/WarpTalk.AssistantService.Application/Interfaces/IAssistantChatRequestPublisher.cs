using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WarpTalk.AssistantService.Application.Interfaces;

public record ChatTurnDto(string Role, string Content);

/// <summary>
/// Publishes a chat turn to the Python ai_assistant_worker chat pipeline over Redis Streams
/// (assistant:chat_requests) — the actual OpenAI tool-calling loop runs there, not in this
/// service. AssistantChatResultConsumerService (API layer) consumes the matching results
/// stream and drives the DB update + SignalR broadcast for whatever comes back.
/// </summary>
public interface IAssistantChatRequestPublisher
{
    Task PublishAsync(
        Guid requestId,
        Guid conversationId,
        Guid workspaceId,
        Guid userId,
        string? bearerToken,
        IReadOnlyList<ChatTurnDto> history,
        string? pageContextJson = null,
        string? mentionsJson = null,
        /// <summary>
        /// WT-474: attachments for this turn, as a JSON array of {dataUrl,name,mimeType}. Not
        /// persisted — see SendAssistantMessageRequest.Attachments. The Redis field keeps the name
        /// images_json for wire compatibility with what the worker already reads.
        /// </summary>
        string? attachmentsJson = null,
        /// <summary>
        /// WT-687: plugin keys switched off for this conversation, as a JSON array, or null.
        /// </summary>
        string? disabledPluginKeysJson = null,
        CancellationToken ct = default);

    /// <summary>
    /// A platform-scope turn (a system admin's WarpBot in the admin portal). Published with
    /// <c>scope=platform</c> and an EMPTY workspace_id: the worker then offers only the read-only
    /// platform admin tools, never workspace retrieval, plugins or workspace tools.
    /// </summary>
    Task PublishPlatformAsync(
        Guid requestId,
        Guid conversationId,
        Guid userId,
        string? bearerToken,
        IReadOnlyList<ChatTurnDto> history,
        CancellationToken ct = default);
}
