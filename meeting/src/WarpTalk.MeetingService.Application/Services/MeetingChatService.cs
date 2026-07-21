using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.MeetingService.Application.DTOs;
using WarpTalk.MeetingService.Application.Interfaces;
using WarpTalk.MeetingService.Application.Mappers;
using WarpTalk.Shared;
using WarpTalk.MeetingService.Domain.Entities;
using WarpTalk.MeetingService.Domain.Interfaces;

namespace WarpTalk.MeetingService.Application.Services;

public class MeetingChatService : IMeetingChatService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IMeetingChatNotifier _chatNotifier;
    private readonly IRedisService _redisService;
    private readonly IChatTranslator _chatTranslator;

    public MeetingChatService(IUnitOfWork unitOfWork, IMeetingChatNotifier chatNotifier, IRedisService redisService, IChatTranslator chatTranslator)
    {
        _unitOfWork = unitOfWork;
        _chatNotifier = chatNotifier;
        _redisService = redisService;
        _chatTranslator = chatTranslator;
    }

    public async Task<Result<IEnumerable<MeetingChatMessageDto>>> GetRoomMessagesAsync(Guid roomId, Guid userId, CancellationToken ct = default)
    {
        var room = await _unitOfWork.MeetingRoomRepository.FirstOrDefaultAsync(r => r.TranslationRoomId == roomId, ct: ct);
        if (room == null)
            return Result.Failure<IEnumerable<MeetingChatMessageDto>>("Room not found.", "NOT_FOUND");

        var participant = await _unitOfWork.MeetingParticipantRepository.FirstOrDefaultAsync(p => p.MeetingRoomId == room.Id && p.UserId == userId, ct: ct);
        bool isParticipant = participant != null;

        if (room.CreatedBy != userId && !isParticipant)
            return Result.Failure<IEnumerable<MeetingChatMessageDto>>("Not a participant.", "FORBIDDEN");

        var messages = await _unitOfWork.MeetingChatMessageRepository.FindAsync(m => m.MeetingRoomId == room.Id, ct: ct);
        
        var dtos = messages.Where(m => !m.IsHidden || room.CreatedBy == userId)
                           .OrderBy(m => m.CreatedAt)
                           .Select(m => m.ToDto());
                           
        return Result.Success<IEnumerable<MeetingChatMessageDto>>(dtos);
    }

    public async Task<Result<MeetingChatMessageDto>> SendMessageAsync(Guid roomId, Guid userId, SendMeetingChatMessageRequest request, CancellationToken ct = default)
    {
        var room = await _unitOfWork.MeetingRoomRepository.FirstOrDefaultAsync(r => r.TranslationRoomId == roomId, ct: ct);
        if (room == null)
            return Result.Failure<MeetingChatMessageDto>("Room not found.", "NOT_FOUND");

        var participant = await _unitOfWork.MeetingParticipantRepository.FirstOrDefaultAsync(p => p.MeetingRoomId == room.Id && p.UserId == userId, ct: ct);
        bool isActiveParticipant = participant != null && participant.IsActive && participant.LeftAt == null;

        if (room.CreatedBy != userId && !isActiveParticipant)
            return Result.Failure<MeetingChatMessageDto>("Not an active participant.", "FORBIDDEN");

        // Resolve WorkspaceId from Redis cache populated by MeetingRoomService on join.
        var workspaceId = Guid.Empty;
        var roomCacheKey = $"meeting:room:{roomId}";
        var cachedRoom = await _redisService.GetCacheAsync<WarpTalk.Shared.Protos.GetTranslationRoomResponse>(roomCacheKey);
        if (cachedRoom.Value != null && Guid.TryParse(cachedRoom.Value.WorkspaceId, out var wsId))
            workspaceId = wsId;

        var message = request.ToEntity(room.Id, workspaceId, userId, participant);

        await _unitOfWork.MeetingChatMessageRepository.AddAsync(message, ct);
        await _unitOfWork.SaveChangesAsync(ct);

        var dto = message.ToDto();
        await _chatNotifier.BroadcastMessageReceivedAsync(roomId, dto, ct);
        
        var agentMentions = request.Mentions.Where(m => m.Type == "agent").ToList();
        if (agentMentions.Any())
        {
            var assistantRequest = new MeetingChatAssistantRequest
            {
                Id = Guid.NewGuid(),
                TriggerMessageId = message.Id,
                MeetingRoomId = room.Id,
                WorkspaceId = workspaceId,
                RequestedByUserId = userId,
                Prompt = request.OriginalText, // Extract prompt from mention if needed
                ContextScope = "recent_messages",
                Status = "pending",
                CreatedAt = DateTime.UtcNow
            };

            await _unitOfWork.MeetingChatAssistantRequestRepository.AddAsync(assistantRequest, ct);
            await _unitOfWork.SaveChangesAsync(ct);

            await _redisService.PublishEventAsync("meeting.chat.assistant_requested", new
            {
                RequestId = assistantRequest.Id,
                RoomId = room.Id,
                MessageId = message.Id,
                UserId = userId,
                Prompt = assistantRequest.Prompt,
                AgentIds = agentMentions.Select(m => m.Id).ToArray()
            });
        }

        return Result.Success<MeetingChatMessageDto>(dto);
    }

    public async Task<Result<MeetingChatTranslationDto>> RequestTranslationAsync(Guid roomId, Guid messageId, Guid userId, TranslateMeetingChatMessageRequest request, CancellationToken ct = default)
    {
        var room = await _unitOfWork.MeetingRoomRepository.FirstOrDefaultAsync(r => r.TranslationRoomId == roomId, ct: ct);
        if (room == null)
            return Result.Failure<MeetingChatTranslationDto>("Room not found.", "NOT_FOUND");

        var participant = await _unitOfWork.MeetingParticipantRepository.FirstOrDefaultAsync(p => p.MeetingRoomId == room.Id && p.UserId == userId, ct: ct);
        bool isActiveParticipant = participant != null && participant.IsActive && participant.LeftAt == null;

        if (room.CreatedBy != userId && !isActiveParticipant)
            return Result.Failure<MeetingChatTranslationDto>("Not an active participant.", "FORBIDDEN");

        var message = await _unitOfWork.MeetingChatMessageRepository.GetByIdAsync(messageId, ct);
        if (message == null || message.MeetingRoomId != room.Id)
            return Result.Failure<MeetingChatTranslationDto>("Message not found.", "NOT_FOUND");

        // Same language — nothing to translate, echo the original back.
        if (string.Equals(message.OriginalLanguage, request.TargetLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return Result.Success(new MeetingChatTranslationDto
            {
                MessageId = messageId,
                TargetLanguage = request.TargetLanguage,
                TranslatedText = message.OriginalText,
                Cached = false,
            });
        }

        // Cache check — a message translated into a given target language once never
        // needs to hit the LLM again for any other viewer requesting the same pair.
        // Keyed on PromptVersion too: bumping it (after a prompt/model change) makes
        // every previously cached row a miss instead of silently serving stale output.
        var existing = await _unitOfWork.MeetingChatTranslationRepository.FirstOrDefaultAsync(
            t => t.MessageId == messageId
                && t.TargetLanguage == request.TargetLanguage
                && t.PromptVersion == _chatTranslator.PromptVersion,
            ct: ct);

        if (existing != null)
        {
            return Result.Success(new MeetingChatTranslationDto
            {
                MessageId = messageId,
                TargetLanguage = request.TargetLanguage,
                TranslatedText = existing.TranslatedText,
                Cached = true,
            });
        }

        var translationResult = await _chatTranslator.TranslateAsync(
            message.OriginalText, message.OriginalLanguage, request.TargetLanguage, ct);

        if (!translationResult.IsSuccess)
            return Result.Failure<MeetingChatTranslationDto>(translationResult.Error, translationResult.ErrorCode);

        var translation = new MeetingChatTranslation
        {
            Id = Guid.NewGuid(),
            MessageId = messageId,
            MeetingRoomId = room.Id,
            SourceLanguage = message.OriginalLanguage,
            TargetLanguage = request.TargetLanguage,
            TranslatedText = translationResult.Value!,
            ModelUsed = _chatTranslator.ModelName,
            PromptVersion = _chatTranslator.PromptVersion,
            CreatedAt = DateTime.UtcNow,
        };

        await _unitOfWork.MeetingChatTranslationRepository.AddAsync(translation, ct);
        await _unitOfWork.SaveChangesAsync(ct);

        return Result.Success(new MeetingChatTranslationDto
        {
            MessageId = messageId,
            TargetLanguage = request.TargetLanguage,
            TranslatedText = translation.TranslatedText,
            Cached = false,
        });
    }

    public async Task<Result<bool>> ModerateMessageAsync(Guid roomId, Guid messageId, Guid userId, ModerateMeetingChatMessageRequest request, CancellationToken ct = default)
    {
        var room = await _unitOfWork.MeetingRoomRepository.FirstOrDefaultAsync(r => r.TranslationRoomId == roomId, ct: ct);
        if (room == null)
            return Result.Failure<bool>("Room not found.", "NOT_FOUND");

        // Only host can moderate
        if (room.CreatedBy != userId)
            return Result.Failure<bool>("Only host can moderate messages.", "FORBIDDEN");

        var message = await _unitOfWork.MeetingChatMessageRepository.GetByIdAsync(messageId, ct);
        if (message == null || message.MeetingRoomId != room.Id)
            return Result.Failure<bool>("Message not found.", "NOT_FOUND");

        if (!message.IsHidden)
        {
            message.IsHidden = true;

            var modEvent = new MeetingChatModerationEvent
            {
                Id = Guid.NewGuid(),
                MessageId = messageId,
                MeetingRoomId = room.Id,
                ModeratedByUserId = userId,
                Action = "hidden",
                Reason = request.Reason,
                CreatedAt = DateTime.UtcNow
            };

            await _unitOfWork.MeetingChatModerationEventRepository.AddAsync(modEvent, ct);
            _unitOfWork.MeetingChatMessageRepository.Update(message);
            await _unitOfWork.SaveChangesAsync(ct);
        }

        await _chatNotifier.BroadcastMessageHiddenAsync(roomId, messageId, ct);

        return Result.Success<bool>(true);
    }
}
