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

/// <inheritdoc cref="IPlatformAssistantConversationService"/>
public class PlatformAssistantConversationService : IPlatformAssistantConversationService
{
    /// <summary>Matches the workspace service, so the history menu reads the same in both scopes.</summary>
    public const int MaxTitleLength = 60;

    /// <summary>One question, not a document. Platform turns carry no attachments to need more.</summary>
    public const int MaxContentLength = 8000;

    private const string NewChatTitle = "New chat";

    private readonly IUnitOfWork _unitOfWork;
    private readonly IAssistantChatRequestPublisher _chatRequestPublisher;

    public PlatformAssistantConversationService(IUnitOfWork unitOfWork, IAssistantChatRequestPublisher chatRequestPublisher)
    {
        _unitOfWork = unitOfWork;
        _chatRequestPublisher = chatRequestPublisher;
    }

    public async Task<Result<IEnumerable<AssistantConversationDto>>> ListConversationsAsync(Guid userId, CancellationToken ct = default)
    {
        var conversations = await _unitOfWork.PlatformConversationRepository.FindAsync(
            c => c.UserId == userId && !c.IsArchived, ct: ct);

        return Result.Success(conversations
            .OrderByDescending(c => c.LastMessageAt ?? c.CreatedAt)
            .Select(c => c.ToDto()));
    }

    public async Task<Result<AssistantConversationDetailDto>> GetConversationAsync(Guid conversationId, Guid userId, CancellationToken ct = default)
    {
        var conversation = await _unitOfWork.PlatformConversationRepository.FirstOrDefaultAsync(
            c => c.Id == conversationId, includeProperties: "Messages", ct: ct);

        if (conversation == null || conversation.UserId != userId)
            return Result.Failure<AssistantConversationDetailDto>("Conversation not found.", ErrorCodes.NotFound);

        return Result.Success(conversation.ToDetailDto());
    }

    public async Task<Result<AssistantConversationDto>> CreateConversationAsync(
        Guid userId, CreatePlatformConversationRequest request, CancellationToken ct = default)
    {
        var title = request?.Title?.Trim();
        var conversation = new PlatformConversation
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Title = string.IsNullOrEmpty(title)
                ? NewChatTitle
                : title.Length > MaxTitleLength ? title[..MaxTitleLength] : title,
            CreatedAt = DateTime.UtcNow,
            IsArchived = false,
        };

        await _unitOfWork.PlatformConversationRepository.AddAsync(conversation, ct);
        await _unitOfWork.SaveChangesAsync(ct);

        return Result.Success(conversation.ToDto());
    }

    public async Task<Result<SendAssistantMessageResponse>> SendMessageAsync(
        Guid conversationId, Guid userId, string? bearerToken, SendPlatformMessageRequest request, CancellationToken ct = default)
    {
        var content = request?.Content?.Trim() ?? string.Empty;
        if (content.Length == 0)
            return Result.Failure<SendAssistantMessageResponse>("Message content is required.", ErrorCodes.ValidationError);
        if (content.Length > MaxContentLength)
            return Result.Failure<SendAssistantMessageResponse>(
                $"A message must be {MaxContentLength} characters or fewer.", ErrorCodes.ValidationError);

        var conversation = await _unitOfWork.PlatformConversationRepository.GetByIdAsync(conversationId, ct);
        if (conversation == null || conversation.UserId != userId)
            return Result.Failure<SendAssistantMessageResponse>("Conversation not found.", ErrorCodes.NotFound);

        var now = DateTime.UtcNow;
        var userMessage = new PlatformMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            UserId = userId,
            Role = "user",
            Content = content,
            Status = "completed",
            CreatedAt = now,
            CompletedAt = now,
        };
        await _unitOfWork.PlatformMessageRepository.AddAsync(userMessage, ct);

        var assistantMessage = new PlatformMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            UserId = null,
            Role = "assistant",
            Content = "",
            Status = "pending",
            CreatedAt = now,
        };
        await _unitOfWork.PlatformMessageRepository.AddAsync(assistantMessage, ct);

        conversation.LastMessageAt = now;
        if (conversation.Title == NewChatTitle)
            conversation.Title = content.Length > MaxTitleLength ? content[..MaxTitleLength] : content;
        _unitOfWork.PlatformConversationRepository.Update(conversation);

        await _unitOfWork.SaveChangesAsync(ct);

        // History comes from THIS conversation's platform rows only. There is no workspace row it
        // could pick up: the query runs against a table that holds none.
        var priorMessages = await _unitOfWork.PlatformMessageRepository.FindAsync(
            m => m.ConversationId == conversationId && m.Status == "completed" && m.Id != userMessage.Id, ct: ct);

        var history = priorMessages
            .OrderBy(m => m.CreatedAt)
            .Select(m => new ChatTurnDto(m.Role, m.Content))
            .Append(new ChatTurnDto("user", userMessage.Content))
            .ToList();

        await _chatRequestPublisher.PublishPlatformAsync(
            assistantMessage.Id, conversationId, userId, bearerToken, history, ct);

        return Result.Success(new SendAssistantMessageResponse(userMessage.Id, assistantMessage.Id));
    }

    public async Task<Result> ArchiveConversationAsync(Guid conversationId, Guid userId, CancellationToken ct = default)
    {
        var conversation = await _unitOfWork.PlatformConversationRepository.GetByIdAsync(conversationId, ct);
        if (conversation == null || conversation.UserId != userId)
            return Result.Failure("Conversation not found.", ErrorCodes.NotFound);

        conversation.IsArchived = true;
        _unitOfWork.PlatformConversationRepository.Update(conversation);
        await _unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result> AuthorizeConversationAccessAsync(Guid conversationId, Guid userId, CancellationToken ct = default)
    {
        var conversation = await _unitOfWork.PlatformConversationRepository.GetByIdAsync(conversationId, ct);
        if (conversation == null || conversation.UserId != userId)
            return Result.Failure("Conversation not found.", ErrorCodes.NotFound);
        return Result.Success();
    }
}
