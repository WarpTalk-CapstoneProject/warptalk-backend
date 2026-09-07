using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.MeetingService.Application.DTOs;
using WarpTalk.MeetingService.Application.Interfaces;
using WarpTalk.MeetingService.Application.Mappers;
using WarpTalk.Shared;
using WarpTalk.MeetingService.Domain.Constants;
using WarpTalk.MeetingService.Domain.Entities;
using WarpTalk.MeetingService.Domain.Interfaces;

namespace WarpTalk.MeetingService.Application.Services;

public class MeetingChatService : IMeetingChatService
{
    private const long MaxFileSizeBytes = 25 * 1024 * 1024; // 25 MB
    private static readonly HashSet<string> BlockedFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".bat", ".cmd", ".sh", ".msi", ".dll", ".scr"
    };

    private readonly IUnitOfWork _unitOfWork;
    private readonly IMeetingChatNotifier _chatNotifier;
    private readonly IRedisService _redisService;
    private readonly IChatTranslator _chatTranslator;
    private readonly IMeetingChatFileStorage _fileStorage;

    public MeetingChatService(IUnitOfWork unitOfWork, IMeetingChatNotifier chatNotifier, IRedisService redisService, IChatTranslator chatTranslator, IMeetingChatFileStorage fileStorage)
    {
        _unitOfWork = unitOfWork;
        _chatNotifier = chatNotifier;
        _redisService = redisService;
        _chatTranslator = chatTranslator;
        _fileStorage = fileStorage;
    }

    public async Task<Result<IEnumerable<MeetingChatMessageDto>>> GetRoomMessagesAsync(Guid roomId, Guid userId, CancellationToken ct = default)
    {
        var room = await _unitOfWork.MeetingRoomRepository.FirstOrDefaultAsync(r => r.TranslationRoomId == roomId, ct: ct);
        if (room == null)
            return Result.Failure<IEnumerable<MeetingChatMessageDto>>("Room not found.", "NOT_FOUND");

        var participant = await _unitOfWork.RtcStreamParticipantRepository.FirstOrDefaultAsync(p => p.MeetingRoomId == room.Id && p.UserId == userId, ct: ct);
        bool isParticipant = participant != null;

        if (room.CreatedBy != userId && !isParticipant)
            return Result.Failure<IEnumerable<MeetingChatMessageDto>>("Not a participant.", "FORBIDDEN");

        var messages = await _unitOfWork.MeetingChatMessageRepository.FindAsync(m => m.MeetingRoomId == room.Id, ct: ct);

        var dtos = messages.Where(m => !m.IsHidden || room.CreatedBy == userId)
                           .OrderBy(m => m.CreatedAt)
                           .Select(m => m.ToDto());

        return Result.Success<IEnumerable<MeetingChatMessageDto>>(dtos);
    }

    public async Task<Result<MeetingChatMessageDto>> SendMessageAsync(Guid roomId, Guid userId, SendMeetingChatMessageRequest request, string? bearerToken = null, CancellationToken ct = default)
    {
        // The column is TEXT and the desktop app posts to this same endpoint, so this check
        // is what actually bounds a message — the editor cap in the web client is only a
        // convenience on top of it (WT-237).
        //
        // Bound once into a local rather than testing request.OriginalText with `?.`: the DTO
        // declares it `null!`, so a null-conditional here tells flow analysis it may be null and
        // turns every later use into CS8601 — which a Release build treats as an error.
        var originalText = request.OriginalText ?? string.Empty;
        if (originalText.Length > MeetingChatConstants.MaxMessageLength)
        {
            return Result.Failure<MeetingChatMessageDto>(
                $"Message must be {MeetingChatConstants.MaxMessageLength} characters or fewer.",
                "VALIDATION_ERROR");
        }

        var room = await _unitOfWork.MeetingRoomRepository.FirstOrDefaultAsync(r => r.TranslationRoomId == roomId, ct: ct);
        if (room == null)
            return Result.Failure<MeetingChatMessageDto>("Room not found.", "NOT_FOUND");

        var participant = await _unitOfWork.RtcStreamParticipantRepository.FirstOrDefaultAsync(p => p.MeetingRoomId == room.Id && p.UserId == userId, ct: ct);
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
                Prompt = originalText, // Extract prompt from mention if needed
                ContextScope = "recent_messages",
                // "queued", not "pending". The lifecycle every other reader speaks is
                // queued → processing → completed/failed, and this was the only place that
                // said "pending" — so MeetingChatAssistantResultConsumerService, which only
                // announces "WarpBot is thinking" for a request still sitting at "queued",
                // never announced anything at all. One word, and the feature was invisible.
                Status = "queued",
                CreatedAt = DateTime.UtcNow
            };

            await _unitOfWork.MeetingChatAssistantRequestRepository.AddAsync(assistantRequest, ct);
            await _unitOfWork.SaveChangesAsync(ct);

            var historyMessages = await _unitOfWork.MeetingChatMessageRepository.FindAsync(
                m => m.MeetingRoomId == room.Id && !m.IsHidden,
                ct: ct) ?? Array.Empty<MeetingChatMessage>();
            var historyJson = JsonSerializer.Serialize(
                historyMessages
                    .OrderBy(m => m.CreatedAt)
                    .TakeLast(20)
                    .Select(m => new
                    {
                        role = m.SenderType == "assistant" ? "assistant" : "user",
                        content = m.OriginalText
                    }));

            var publishResult = await _redisService.PublishStreamMessageAsync(
                "assistant:chat_requests",
                new Dictionary<string, string>
                {
                    ["request_id"] = assistantRequest.Id.ToString(),
                    ["conversation_id"] = room.Id.ToString(),
                    ["workspace_id"] = workspaceId.ToString(),
                    ["user_id"] = userId.ToString(),
                    ["origin"] = "meeting_chat",
                    ["bearer_token"] = bearerToken ?? string.Empty,
                    ["history_json"] = historyJson,
                    ["page_context_json"] = JsonSerializer.Serialize(new
                    {
                        pageType = "meeting_chat",
                        entityId = roomId,
                        workspaceId
                    }),
                    ["mentions_json"] = JsonSerializer.Serialize(agentMentions),
                    ["timestamp_ms"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                        .ToString(CultureInfo.InvariantCulture)
                });

            if (publishResult?.IsSuccess != true)
            {
                assistantRequest.Status = "failed";
                assistantRequest.CompletedAt = DateTime.UtcNow;
                _unitOfWork.MeetingChatAssistantRequestRepository.Update(assistantRequest);

                var failureResponse = new MeetingChatMessage
                {
                    Id = Guid.NewGuid(),
                    MeetingRoomId = room.Id,
                    WorkspaceId = workspaceId,
                    SenderDisplayName = "WarpBot",
                    SenderType = "assistant",
                    MessageType = "assistant_response",
                    OriginalLanguage = request.OriginalLanguage,
                    OriginalText = "WarpBot is temporarily unavailable. Please try again shortly.",
                    TranslationEnabled = false,
                    IsHidden = false,
                    Mentions = "[]",
                    CreatedAt = DateTime.UtcNow
                };

                await _unitOfWork.MeetingChatMessageRepository.AddAsync(failureResponse, ct);
                await _unitOfWork.SaveChangesAsync(ct);
                await _chatNotifier.BroadcastMessageReceivedAsync(roomId, failureResponse.ToDto(), ct);
            }
        }

        return Result.Success<MeetingChatMessageDto>(dto);
    }

    public async Task<Result<MeetingChatTranslationDto>> RequestTranslationAsync(Guid roomId, Guid messageId, Guid userId, TranslateMeetingChatMessageRequest request, CancellationToken ct = default)
    {
        var room = await _unitOfWork.MeetingRoomRepository.FirstOrDefaultAsync(r => r.TranslationRoomId == roomId, ct: ct);
        if (room == null)
            return Result.Failure<MeetingChatTranslationDto>("Room not found.", "NOT_FOUND");

        var participant = await _unitOfWork.RtcStreamParticipantRepository.FirstOrDefaultAsync(p => p.MeetingRoomId == room.Id && p.UserId == userId, ct: ct);
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
            return Result.Failure<MeetingChatTranslationDto>(
                translationResult.Error ?? "Translation failed.",
                translationResult.ErrorCode);

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

    public async Task<Result<MeetingChatMessageDto>> UploadFileAsync(Guid roomId, Guid userId, UploadMeetingChatFileRequest request, CancellationToken ct = default)
    {
        var file = request.File;
        if (file == null || file.Length <= 0)
            return Result.Failure<MeetingChatMessageDto>("The file is empty.", "VALIDATION_ERROR");

        if (file.Length > MaxFileSizeBytes)
            return Result.Failure<MeetingChatMessageDto>("The file exceeds the 25 MB limit.", "VALIDATION_ERROR");

        var extension = Path.GetExtension(file.FileName);
        if (BlockedFileExtensions.Contains(extension))
            return Result.Failure<MeetingChatMessageDto>("This file type is not allowed.", "VALIDATION_ERROR");

        var room = await _unitOfWork.MeetingRoomRepository.FirstOrDefaultAsync(r => r.TranslationRoomId == roomId, ct: ct);
        if (room == null)
            return Result.Failure<MeetingChatMessageDto>("Room not found.", "NOT_FOUND");

        var participant = await _unitOfWork.RtcStreamParticipantRepository.FirstOrDefaultAsync(p => p.MeetingRoomId == room.Id && p.UserId == userId, ct: ct);
        bool isActiveParticipant = participant != null && participant.IsActive && participant.LeftAt == null;

        if (room.CreatedBy != userId && !isActiveParticipant)
            return Result.Failure<MeetingChatMessageDto>("Not an active participant.", "FORBIDDEN");

        var workspaceId = Guid.Empty;
        var roomCacheKey = $"meeting:room:{roomId}";
        var cachedRoom = await _redisService.GetCacheAsync<WarpTalk.Shared.Protos.GetTranslationRoomResponse>(roomCacheKey);
        if (cachedRoom.Value != null && Guid.TryParse(cachedRoom.Value.WorkspaceId, out var wsId))
            workspaceId = wsId;

        var messageId = Guid.NewGuid();
        var storageKey = $"{room.Id}/{messageId}{extension}";
        var fileUrl = $"/meetings/rooms/{roomId}/chat/files/{messageId}/download";

        using (var stream = file.OpenReadStream())
        {
            await _fileStorage.SaveAsync(storageKey, stream, ct);
        }

        var message = MeetingChatMapper.ToFileEntity(
            messageId, room.Id, workspaceId, userId, participant,
            fileUrl, file.FileName, file.Length, file.ContentType);

        try
        {
            await _unitOfWork.MeetingChatMessageRepository.AddAsync(message, ct);
            await _unitOfWork.SaveChangesAsync(ct);
        }
        catch
        {
            await _fileStorage.DeleteAsync(storageKey, ct);
            throw;
        }

        var dto = message.ToDto();
        await _chatNotifier.BroadcastMessageReceivedAsync(roomId, dto, ct);

        return Result.Success(dto);
    }

    public async Task<Result<MeetingChatFileDownloadResult>> DownloadFileAsync(Guid roomId, Guid messageId, Guid userId, CancellationToken ct = default)
    {
        var room = await _unitOfWork.MeetingRoomRepository.FirstOrDefaultAsync(r => r.TranslationRoomId == roomId, ct: ct);
        if (room == null)
            return Result.Failure<MeetingChatFileDownloadResult>("Room not found.", "NOT_FOUND");

        var participant = await _unitOfWork.RtcStreamParticipantRepository.FirstOrDefaultAsync(p => p.MeetingRoomId == room.Id && p.UserId == userId, ct: ct);
        bool isParticipant = participant != null;

        if (room.CreatedBy != userId && !isParticipant)
            return Result.Failure<MeetingChatFileDownloadResult>("Not a participant.", "FORBIDDEN");

        var message = await _unitOfWork.MeetingChatMessageRepository.GetByIdAsync(messageId, ct);
        if (message == null || message.MeetingRoomId != room.Id || message.MessageType != "file" || string.IsNullOrEmpty(message.FileName))
            return Result.Failure<MeetingChatFileDownloadResult>("File not found.", "NOT_FOUND");

        var storageKey = $"{room.Id}/{message.Id}{Path.GetExtension(message.FileName)}";
        var stream = await _fileStorage.OpenReadAsync(storageKey, ct);

        return Result.Success(new MeetingChatFileDownloadResult
        {
            Stream = stream,
            ContentType = message.ContentType ?? "application/octet-stream",
            FileName = message.FileName
        });
    }
}
