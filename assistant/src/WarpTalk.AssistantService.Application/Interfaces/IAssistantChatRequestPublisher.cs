using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WarpTalk.AssistantService.Application.Interfaces;

public sealed record ChatTurnDto(string Role, string Content);

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
        CancellationToken ct = default);
}
