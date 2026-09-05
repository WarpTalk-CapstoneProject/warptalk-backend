using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Helpers;
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
        // WT-474: an attachment IS a question. "What is this?" with a screenshot, or a PDF dropped
        // in with nothing typed, is a complete turn — so content is required only when nothing is
        // attached. Without this the browser would offer an attachment-only send that the server
        // refuses, which is the worst of both.
        var hasAttachment = request.Attachments is { Count: > 0 };
        if (string.IsNullOrWhiteSpace(request.Content) && !hasAttachment)
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
        if (conversation.Title == "New chat" && !string.IsNullOrWhiteSpace(request.Content))
        {
            // WT-474: guarded on non-empty, because an attachment-only first turn has no text to
            // title the conversation with — and "" is a worse title than "New chat", which at least
            // reads as a conversation waiting to be named.
            conversation.Title = request.Content.Length > 60 ? request.Content[..60] : request.Content;
        }
        _unitOfWork.AssistantConversationRepository.Update(conversation);

        await _unitOfWork.SaveChangesAsync(ct);

        var priorMessages = await _unitOfWork.AssistantMessageRepository.FindAsync(
            m => m.ConversationId == conversationId && m.Status == "completed" && m.Id != userMessage.Id, ct: ct);

        var history = priorMessages
            .OrderBy(m => m.CreatedAt)
            .Select(m => new ChatTurnDto(m.Role, m.Content))
            .Append(new ChatTurnDto("user", userMessage.Content))
            .ToList();

        var pageContextJson = AssistantConversationPayloadSerializer.SerializePageContext(request.PageContext, conversation.WorkspaceId);
        var mentionsJson = AssistantConversationPayloadSerializer.SerializeMentions(request.Mentions, conversation.WorkspaceId);
        var attachmentsJson = AssistantConversationPayloadSerializer.SerializeAttachments(request.Attachments);

        await _chatRequestPublisher.PublishAsync(
            assistantMessage.Id, conversationId, conversation.WorkspaceId, userId, bearerToken, history, pageContextJson, mentionsJson, attachmentsJson, ct);

        return Result.Success(new SendAssistantMessageResponse(userMessage.Id, assistantMessage.Id));
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
