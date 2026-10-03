using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Application.Interfaces;

/// <summary>
/// Platform-scope WarpBot conversations: a system administrator asking about the whole platform
/// from the admin portal.
///
/// AUTHORIZATION IS THE CALLER'S JOB, AND IT IS NOT OPTIONAL. Every entry point — the
/// PlatformAssistantConversationsController and AssistantHub.JoinConversation — checks the
/// WarpTalkSystemAdmin policy before calling in. This service then scopes every read and write to
/// the caller's own rows, exactly as the workspace service does, and reads and writes ONLY the
/// platform tables: it has no way to reach a workspace conversation, nor the workspace service
/// this one.
/// </summary>
public interface IPlatformAssistantConversationService
{
    Task<Result<IEnumerable<AssistantConversationDto>>> ListConversationsAsync(Guid userId, CancellationToken ct = default);
    Task<Result<AssistantConversationDetailDto>> GetConversationAsync(Guid conversationId, Guid userId, CancellationToken ct = default);
    Task<Result<AssistantConversationDto>> CreateConversationAsync(Guid userId, CreatePlatformConversationRequest request, CancellationToken ct = default);
    Task<Result<SendAssistantMessageResponse>> SendMessageAsync(Guid conversationId, Guid userId, string? bearerToken, SendPlatformMessageRequest request, CancellationToken ct = default);
    Task<Result> ArchiveConversationAsync(Guid conversationId, Guid userId, CancellationToken ct = default);
    Task<Result> AuthorizeConversationAccessAsync(Guid conversationId, Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Stop the reply <paramref name="messageId"/> while it is still being written. Idempotent: a
    /// reply that already finished has nothing to stop and still answers success.
    /// </summary>
    Task<Result> StopReplyAsync(Guid conversationId, Guid messageId, Guid userId, CancellationToken ct = default);
}
