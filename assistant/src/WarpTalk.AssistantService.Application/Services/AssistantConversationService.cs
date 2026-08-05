using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Application.Mappers;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Application.Services;

public class AssistantConversationService : IAssistantConversationService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IAssistantChatRequestPublisher _chatRequestPublisher;

    public AssistantConversationService(IUnitOfWork unitOfWork, IAssistantChatRequestPublisher chatRequestPublisher)
    {
        _unitOfWork = unitOfWork;
        _chatRequestPublisher = chatRequestPublisher;
    }

    public async Task<Result<IEnumerable<AssistantConversationDto>>> ListConversationsAsync(Guid workspaceId, Guid userId, CancellationToken ct = default)
    {
        var conversations = await _unitOfWork.AssistantConversationRepository.FindAsync(
            c => c.WorkspaceId == workspaceId && c.UserId == userId && !c.IsArchived, ct: ct);

        var dtos = conversations
            .OrderByDescending(c => c.LastMessageAt ?? c.CreatedAt)
            .Select(c => c.ToDto());

        return Result.Success(dtos);
    }

    public async Task<Result<AssistantConversationDetailDto>> GetConversationAsync(Guid conversationId, Guid userId, CancellationToken ct = default)
    {
        var conversation = await _unitOfWork.AssistantConversationRepository.FirstOrDefaultAsync(
            c => c.Id == conversationId, includeProperties: "Messages", ct: ct);

        if (conversation == null || conversation.UserId != userId)
            return Result.Failure<AssistantConversationDetailDto>("Conversation not found.", "NOT_FOUND");

        return Result.Success(conversation.ToDetailDto());
    }

    public async Task<Result<AssistantConversationDto>> CreateConversationAsync(Guid workspaceId, Guid userId, CancellationToken ct = default)
    {
        var conversation = new AssistantConversation
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            UserId = userId,
            Title = "New chat",
            CreatedAt = DateTime.UtcNow,
            IsArchived = false,
        };

        await _unitOfWork.AssistantConversationRepository.AddAsync(conversation, ct);
        await _unitOfWork.SaveChangesAsync(ct);

        return Result.Success(conversation.ToDto());
    }

    public async Task<Result<SendAssistantMessageResponse>> SendMessageAsync(Guid conversationId, Guid userId, string? bearerToken, SendAssistantMessageRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Content))
            return Result.Failure<SendAssistantMessageResponse>("Message content is required.", "VALIDATION_ERROR");

        var conversation = await _unitOfWork.AssistantConversationRepository.GetByIdAsync(conversationId, ct);
        if (conversation == null || conversation.UserId != userId)
            return Result.Failure<SendAssistantMessageResponse>("Conversation not found.", "NOT_FOUND");

        var now = DateTime.UtcNow;

        var userMessage = new AssistantMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            WorkspaceId = conversation.WorkspaceId,
            UserId = userId,
            Role = "user",
            Content = request.Content,
            Status = "completed",
            CreatedAt = now,
            CompletedAt = now,
        };
        await _unitOfWork.AssistantMessageRepository.AddAsync(userMessage, ct);

        var assistantMessage = new AssistantMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            WorkspaceId = conversation.WorkspaceId,
            UserId = null,
            Role = "assistant",
            Content = "",
            Status = "pending",
            CreatedAt = now,
        };
        await _unitOfWork.AssistantMessageRepository.AddAsync(assistantMessage, ct);

        conversation.LastMessageAt = now;
        if (conversation.Title == "New chat")
            conversation.Title = request.Content.Length > 60 ? request.Content[..60] : request.Content;
        _unitOfWork.AssistantConversationRepository.Update(conversation);

        await _unitOfWork.SaveChangesAsync(ct);

        var priorMessages = await _unitOfWork.AssistantMessageRepository.FindAsync(
            m => m.ConversationId == conversationId && m.Status == "completed" && m.Id != userMessage.Id, ct: ct);

        var history = priorMessages
            .OrderBy(m => m.CreatedAt)
            .Select(m => new ChatTurnDto(m.Role, m.Content))
            .Append(new ChatTurnDto("user", userMessage.Content))
            .ToList();

        var pageContextJson = SerializePageContext(request.PageContext, conversation.WorkspaceId);
        var mentionsJson = SerializeMentions(request.Mentions, conversation.WorkspaceId);

        await _chatRequestPublisher.PublishAsync(
            assistantMessage.Id, conversationId, conversation.WorkspaceId, userId, bearerToken, history, pageContextJson, mentionsJson, ct);

        return Result.Success(new SendAssistantMessageResponse(userMessage.Id, assistantMessage.Id));
    }

    /// <summary>
    /// Serializes the frontend's ambient page-context hint for the Python worker, scoping it
    /// to the conversation's own workspace — a client can't smuggle in ambient context from a
    /// workspace it isn't even chatting in. This is the .NET-side authority check; anything the
    /// assistant needs beyond this thin snapshot is fetched by a tool using the caller's own
    /// bearer token, not from this payload.
    /// </summary>
    private static string? SerializePageContext(AssistantPageContextDto? pageContext, Guid conversationWorkspaceId)
    {
        if (pageContext == null || string.IsNullOrWhiteSpace(pageContext.PageType))
            return null;

        if (!string.IsNullOrEmpty(pageContext.WorkspaceId)
            && Guid.TryParse(pageContext.WorkspaceId, out var contextWorkspaceId)
            && contextWorkspaceId != conversationWorkspaceId)
        {
            return null;
        }

        return JsonSerializer.Serialize(new
        {
            pageType = pageContext.PageType,
            entityId = pageContext.EntityId,
            workspaceId = conversationWorkspaceId.ToString(),
            snapshot = pageContext.Snapshot,
        });
    }

    /// <summary>
    /// Serializes the frontend's explicit @mentions for the Python worker, stamping the
    /// conversation's own workspace id onto every entry — mirrors SerializePageContext's
    /// authority check so a mention can't claim to belong to a workspace this conversation
    /// isn't even in. Entries missing EntityType/EntityId are dropped rather than failing
    /// the whole request.
    /// </summary>
    private static string? SerializeMentions(List<AssistantMentionDto>? mentions, Guid conversationWorkspaceId)
    {
        if (mentions == null || mentions.Count == 0)
            return null;

        var sanitized = mentions
            .Where(m => !string.IsNullOrWhiteSpace(m.EntityType) && !string.IsNullOrWhiteSpace(m.EntityId))
            .Select(m => new
            {
                entityType = m.EntityType,
                entityId = m.EntityId,
                label = m.Label,
                workspaceId = conversationWorkspaceId.ToString(),
            })
            .ToList();

        return sanitized.Count == 0 ? null : JsonSerializer.Serialize(sanitized);
    }

    public async Task<Result> ArchiveConversationAsync(Guid conversationId, Guid userId, CancellationToken ct = default)
    {
        var conversation = await _unitOfWork.AssistantConversationRepository.GetByIdAsync(conversationId, ct);
        if (conversation == null || conversation.UserId != userId)
            return Result.Failure("Conversation not found.", "NOT_FOUND");

        conversation.IsArchived = true;
        _unitOfWork.AssistantConversationRepository.Update(conversation);
        await _unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }

    public async Task<Result> AuthorizeConversationAccessAsync(Guid conversationId, Guid userId, CancellationToken ct = default)
    {
        var conversation = await _unitOfWork.AssistantConversationRepository.GetByIdAsync(conversationId, ct);
        if (conversation == null || conversation.UserId != userId)
            return Result.Failure("Conversation not found.", "NOT_FOUND");

        return Result.Success();
    }
}
