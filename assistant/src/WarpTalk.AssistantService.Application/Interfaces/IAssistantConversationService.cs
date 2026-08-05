using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Application.Interfaces;

public interface IAssistantConversationService
{
    Task<Result<IEnumerable<AssistantConversationDto>>> ListConversationsAsync(Guid workspaceId, Guid userId, CancellationToken ct = default);
    Task<Result<AssistantConversationDetailDto>> GetConversationAsync(Guid conversationId, Guid userId, CancellationToken ct = default);
    Task<Result<AssistantConversationDto>> CreateConversationAsync(Guid workspaceId, Guid userId, CancellationToken ct = default);
    Task<Result<SendAssistantMessageResponse>> SendMessageAsync(Guid conversationId, Guid userId, string? bearerToken, SendAssistantMessageRequest request, CancellationToken ct = default);
    Task<Result> ArchiveConversationAsync(Guid conversationId, Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Confirms the conversation exists and belongs to the user, without loading its
    /// messages. Used by the SignalR hub before joining a conversation group, which
    /// needs the ownership check but none of the conversation payload.
    /// </summary>
    Task<Result> AuthorizeConversationAccessAsync(Guid conversationId, Guid userId, CancellationToken ct = default);
}
