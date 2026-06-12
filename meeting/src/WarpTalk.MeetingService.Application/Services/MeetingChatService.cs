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

    public MeetingChatService(IUnitOfWork unitOfWork, IMeetingChatNotifier chatNotifier, IRedisService redisService)
    {
        _unitOfWork = unitOfWork;
        _chatNotifier = chatNotifier;
        _redisService = redisService;
    }

    public async Task<Result<IEnumerable<MeetingChatMessageDto>>> GetRoomMessagesAsync(Guid roomId, Guid userId, CancellationToken ct = default)
    {
        var room = await _unitOfWork.MeetingRoomRepository.GetByIdAsync(roomId, ct);
        if (room == null)
            return Result.Failure<IEnumerable<MeetingChatMessageDto>>("Room not found.", "NOT_FOUND");

        var participant = await _unitOfWork.MeetingParticipantRepository.FirstOrDefaultAsync(p => p.MeetingRoomId == roomId && p.UserId == userId, ct: ct);
        bool isActiveParticipant = participant != null && participant.IsActive && participant.LeftAt == null;

        if (room.CreatedBy != userId && !isActiveParticipant)
            return Result.Failure<IEnumerable<MeetingChatMessageDto>>("Not an active participant.", "FORBIDDEN");

        var messages = await _unitOfWork.MeetingChatMessageRepository.FindAsync(m => m.MeetingRoomId == roomId, ct: ct);
        
        var dtos = messages.Where(m => !m.IsHidden || room.CreatedBy == userId)
                           .OrderBy(m => m.CreatedAt)
                           .Select(m => m.ToDto());
                           
        return Result.Success<IEnumerable<MeetingChatMessageDto>>(dtos);
    }

    public async Task<Result<MeetingChatMessageDto>> SendMessageAsync(Guid roomId, Guid userId, SendMeetingChatMessageRequest request, CancellationToken ct = default)
    {
        var room = await _unitOfWork.MeetingRoomRepository.GetByIdAsync(roomId, ct);
        if (room == null)
            return Result.Failure<MeetingChatMessageDto>("Room not found.", "NOT_FOUND");

        var participant = await _unitOfWork.MeetingParticipantRepository.FirstOrDefaultAsync(p => p.MeetingRoomId == roomId && p.UserId == userId, ct: ct);
        bool isActiveParticipant = participant != null && participant.IsActive && participant.LeftAt == null;

        if (room.CreatedBy != userId && !isActiveParticipant)
            return Result.Failure<MeetingChatMessageDto>("Not an active participant.", "FORBIDDEN");

        // For Capstone, WorkspaceId might not be directly on MeetingRoom, but we can assume a placeholder or get it.
        // Assuming Guid.Empty for workspaceId if not available, or get from somewhere else.
        var workspaceId = Guid.Empty; 

        var message = request.ToEntity(roomId, workspaceId, userId, participant);

        await _unitOfWork.MeetingChatMessageRepository.AddAsync(message, ct);
        await _unitOfWork.SaveChangesAsync(ct);

        var dto = message.ToDto();
        await _chatNotifier.BroadcastMessageReceivedAsync(roomId, dto, ct);
        
        if (request.ContainsWarpbotMention)
        {
            var assistantRequest = new MeetingChatAssistantRequest
            {
                Id = Guid.NewGuid(),
                TriggerMessageId = message.Id,
                MeetingRoomId = roomId,
                WorkspaceId = workspaceId,
                RequestedByUserId = userId,
                Prompt = request.OriginalText, // or extract prompt from mention
                ContextScope = "recent_messages",
                Status = "pending",
                CreatedAt = DateTime.UtcNow
            };

            await _unitOfWork.MeetingChatAssistantRequestRepository.AddAsync(assistantRequest, ct);
            await _unitOfWork.SaveChangesAsync(ct);

            await _redisService.PublishEventAsync("meeting.chat.assistant_requested", new
            {
                RequestId = assistantRequest.Id,
                RoomId = roomId,
                MessageId = message.Id,
                UserId = userId,
                Prompt = assistantRequest.Prompt
            });
        }
        else if (request.TranslationEnabled)
        {
            // Auto-translate could be handled here if requested
            await _redisService.PublishEventAsync("meeting.chat.translation_requested", new
            {
                MessageId = message.Id,
                RoomId = roomId,
                Text = message.OriginalText,
                SourceLanguage = message.OriginalLanguage,
                TargetLanguage = "auto" // or specific default
            });
        }
        
        return Result.Success<MeetingChatMessageDto>(dto);
    }

    public async Task<Result<bool>> RequestTranslationAsync(Guid roomId, Guid messageId, Guid userId, TranslateMeetingChatMessageRequest request, CancellationToken ct = default)
    {
        var room = await _unitOfWork.MeetingRoomRepository.GetByIdAsync(roomId, ct);
        if (room == null)
            return Result.Failure<bool>("Room not found.", "NOT_FOUND");

        var participant = await _unitOfWork.MeetingParticipantRepository.FirstOrDefaultAsync(p => p.MeetingRoomId == roomId && p.UserId == userId, ct: ct);
        bool isActiveParticipant = participant != null && participant.IsActive && participant.LeftAt == null;

        if (room.CreatedBy != userId && !isActiveParticipant)
            return Result.Failure<bool>("Not an active participant.", "FORBIDDEN");

        var message = await _unitOfWork.MeetingChatMessageRepository.GetByIdAsync(messageId, ct);
        if (message == null || message.MeetingRoomId != roomId)
            return Result.Failure<bool>("Message not found.", "NOT_FOUND");

        // Publish to Redis for async translation.
        await _redisService.PublishEventAsync("meeting.chat.translation_requested", new
        {
            MessageId = message.Id,
            RoomId = roomId,
            Text = message.OriginalText,
            SourceLanguage = message.OriginalLanguage,
            TargetLanguage = request.TargetLanguage
        });
        
        return Result.Success<bool>(true);
    }

    public async Task<Result<bool>> ModerateMessageAsync(Guid roomId, Guid messageId, Guid userId, ModerateMeetingChatMessageRequest request, CancellationToken ct = default)
    {
        var room = await _unitOfWork.MeetingRoomRepository.GetByIdAsync(roomId, ct);
        if (room == null)
            return Result.Failure<bool>("Room not found.", "NOT_FOUND");

        // Only host can moderate
        if (room.CreatedBy != userId)
            return Result.Failure<bool>("Only host can moderate messages.", "FORBIDDEN");

        var message = await _unitOfWork.MeetingChatMessageRepository.GetByIdAsync(messageId, ct);
        if (message == null || message.MeetingRoomId != roomId)
            return Result.Failure<bool>("Message not found.", "NOT_FOUND");

        if (!message.IsHidden)
        {
            message.IsHidden = true;

            var modEvent = new MeetingChatModerationEvent
            {
                Id = Guid.NewGuid(),
                MessageId = messageId,
                MeetingRoomId = roomId,
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
