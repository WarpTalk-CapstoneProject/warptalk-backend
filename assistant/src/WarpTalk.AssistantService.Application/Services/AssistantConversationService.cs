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

    /// <summary>How many turns a handed-over conversation may start with. The worker only ever
    /// needs the recent thread, and this is the one request that writes many rows at once.</summary>
    public const int MaxSeedMessages = 40;

    /// <summary>Per seeded turn. A WarpBot answer is a few paragraphs; a book is not a turn.</summary>
    public const int MaxSeedMessageLength = 8000;

    /// <summary>Matches the 60 characters SendMessageAsync titles a conversation with.</summary>
    public const int MaxTitleLength = 60;

    private static readonly HashSet<string> SeedRoles = new(StringComparer.Ordinal) { "user", "assistant" };

    public async Task<Result<AssistantConversationDto>> CreateConversationAsync(
        Guid userId, CreateAssistantConversationRequest request, CancellationToken ct = default)
    {
        var seeds = request.SeedMessages ?? new List<AssistantSeedMessageDto>();
        if (seeds.Count > MaxSeedMessages)
            return Result.Failure<AssistantConversationDto>(
                $"A conversation can start with at most {MaxSeedMessages} messages.", "VALIDATION_ERROR");

        foreach (var seed in seeds)
        {
            // Refused rather than dropped: "system" or "tool" here is somebody trying to write the
            // prompt, and quietly keeping the rest would hide that the request was not what it said.
            if (seed is null || !SeedRoles.Contains(seed.Role ?? string.Empty))
                return Result.Failure<AssistantConversationDto>(
                    "A seeded message's role must be \"user\" or \"assistant\".", "VALIDATION_ERROR");
            if ((seed.Content ?? string.Empty).Length > MaxSeedMessageLength)
                return Result.Failure<AssistantConversationDto>(
                    $"A seeded message must be {MaxSeedMessageLength} characters or fewer.", "VALIDATION_ERROR");
        }

        var now = DateTime.UtcNow;
        var title = request.Title?.Trim();
        var conversation = new AssistantConversation
        {
            Id = Guid.NewGuid(),
            WorkspaceId = request.WorkspaceId,
            UserId = userId,
            Title = string.IsNullOrEmpty(title)
                ? "New chat"
                : title.Length > MaxTitleLength ? title[..MaxTitleLength] : title,
            CreatedAt = now,
            IsArchived = false,
        };

        await _unitOfWork.AssistantConversationRepository.AddAsync(conversation, ct);

        // Blank turns carry nothing to the model and would render as empty bubbles, so they are
        // skipped. What survives is stamped a millisecond apart, BEFORE now: history is read back
        // ordered by CreatedAt, and the first question asked in the widget must sort after them.
        var kept = seeds.Where(seed => !string.IsNullOrWhiteSpace(seed.Content)).ToList();
        for (var index = 0; index < kept.Count; index++)
        {
            var seed = kept[index];
            var stamp = now.AddMilliseconds(index - kept.Count);
            await _unitOfWork.AssistantMessageRepository.AddAsync(new AssistantMessage
            {
                Id = Guid.NewGuid(),
                ConversationId = conversation.Id,
                WorkspaceId = conversation.WorkspaceId,
                UserId = seed.Role == "user" ? userId : null,
                Role = seed.Role,
                Content = seed.Content.Trim(),
                Status = "completed",
                CreatedAt = stamp,
                CompletedAt = stamp,
            }, ct);
        }

        if (kept.Count > 0) conversation.LastMessageAt = now;

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

        // Serialized once, before the message is written, so the row and the worker receive the
        // SAME string: what the thread shows on reload cannot drift from what WarpBot was told.
        var mentionsJson = AssistantConversationPayloadSerializer.SerializeMentions(request.Mentions, conversation.WorkspaceId);

        var userMessage = new AssistantMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            WorkspaceId = conversation.WorkspaceId,
            UserId = userId,
            Role = "user",
            Content = request.Content,
            MentionsJson = mentionsJson,
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
        var attachmentsJson = AssistantConversationPayloadSerializer.SerializeAttachments(request.Attachments);
        var disabledPluginKeysJson = AssistantConversationPayloadSerializer.SerializeDisabledPluginKeys(request.DisabledPluginKeys);

        await _chatRequestPublisher.PublishAsync(
            assistantMessage.Id, conversationId, conversation.WorkspaceId, userId, bearerToken, history, pageContextJson, mentionsJson, attachmentsJson, disabledPluginKeysJson, ct);

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
