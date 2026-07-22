using System;
using System.Collections.Generic;
using System.Linq;
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
    private readonly IAssistantAgentJobQueue _jobQueue;

    public AssistantConversationService(IUnitOfWork unitOfWork, IAssistantAgentJobQueue jobQueue)
    {
        _unitOfWork = unitOfWork;
        _jobQueue = jobQueue;
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

    public async Task<Result<SendAssistantMessageResponse>> SendMessageAsync(Guid conversationId, Guid userId, SendAssistantMessageRequest request, CancellationToken ct = default)
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

        await _jobQueue.EnqueueAsync(
            new AssistantAgentJob(conversationId, assistantMessage.Id, conversation.WorkspaceId, userId), ct);

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
}
