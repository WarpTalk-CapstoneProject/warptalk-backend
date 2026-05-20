using System;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.LanguagePolicy;
using WarpTalk.TranslationRoomService.Application.Mappers;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using WarpTalk.TranslationRoomService.Domain.ValueObjects;

namespace WarpTalk.TranslationRoomService.Application.Services;

public class TranslationRoomService : ITranslationRoomService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITranslationRoomRepository _translationRoomRepository;
    private readonly ITranslationRoomParticipantRepository _participantRepository;
    private readonly ILanguagePolicy _languagePolicy;
    private readonly IAudioRouteEventProcessor _audioRouteEventProcessor;
    private readonly ILogger<TranslationRoomService> _logger;

    public TranslationRoomService(IUnitOfWork unitOfWork, ILanguagePolicy languagePolicy, IAudioRouteEventProcessor audioRouteEventProcessor, ILogger<TranslationRoomService> logger)
    {
        _unitOfWork = unitOfWork;
        _languagePolicy = languagePolicy;
        _audioRouteEventProcessor = audioRouteEventProcessor;
        _translationRoomRepository = _unitOfWork.TranslationRoomRepository;
        _participantRepository = _unitOfWork.TranslationRoomParticipantRepository;
        _logger = logger;
    }

    public async Task<Result<TranslationRoomDto>> CreateTranslationRoomAsync(CreateTranslationRoomRequest request, Guid hostId, CancellationToken ct = default)
    {
        try
        {
            // WT-65: Fallback to user settings if languages are missing
            var sourceLang = request.SourceLanguage;
            var targetLangs = request.TargetLanguages;

            if (string.IsNullOrWhiteSpace(sourceLang) || targetLangs == null || !targetLangs.Any())
            {
                var userDefaults = await _unitOfWork.UserSettingsRepository.GetDefaultsAsync(hostId, ct);
                if (userDefaults != null)
                {
                    sourceLang ??= userDefaults.Value.DefaultSpeakLanguage;
                    if (targetLangs == null || !targetLangs.Any())
                    {
                        targetLangs = new List<string> { userDefaults.Value.DefaultListenLanguage };
                    }
                }
            }

            // WT-65: Validate Source Language
            if (string.IsNullOrWhiteSpace(sourceLang))
                return Result.Failure<TranslationRoomDto>(TranslationRoomConstants.ValidationSourceLanguageRequired, ErrorCodes.ValidationError);

            if (!await _languagePolicy.IsSupportedAsync(sourceLang))
                return Result.Failure<TranslationRoomDto>(TranslationRoomConstants.ValidationSourceLanguageUnsupported, ErrorCodes.ValidationError);

            // WT-65: Validate Target Languages
            if (targetLangs == null || !targetLangs.Any())
                return Result.Failure<TranslationRoomDto>(TranslationRoomConstants.ValidationTargetLanguagesRequired, ErrorCodes.ValidationError);

            foreach (var lang in targetLangs)
            {
                if (!await _languagePolicy.IsSupportedAsync(lang))
                    return Result.Failure<TranslationRoomDto>(string.Format(TranslationRoomConstants.ValidationLanguageUnsupported, lang), ErrorCodes.ValidationError);
            }

            // 1. Determine initial status
            var status = request.ScheduledAt.HasValue ? nameof(RoomStatus.SCHEDULED) : nameof(RoomStatus.WAITING);

            // 2. Generate unique 12-char alphanumeric TranslationRoomCode
            string roomCode;
            bool exists;
            do
            {
                roomCode = RoomCodeGenerator.GenerateCode();
                exists = await _translationRoomRepository.ExistsByCodeAsync(roomCode, TranslationRoomConstants.TerminalStatuses, ct);
            } while (exists);

            // 3. Create entity
            var room = request.ToEntity(hostId, roomCode, status, sourceLang, targetLangs);

            // 4. Save via repository and UnitOfWork
            await _translationRoomRepository.AddAsync(room, ct);
            await _unitOfWork.SaveChangesAsync(ct);

            // 5. Return mapped response
            return Result.Success(room.ToResponseDto());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while creating translation room for HostId: {HostId}", hostId);
            return Result.Failure<TranslationRoomDto>($"Error: {ex.Message} Inner: {ex.InnerException?.Message}", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<TranslationRoomDto>> GetTranslationRoomAsync(Guid translationRoomId, CancellationToken ct = default)
    {
        try
        {
            var translationRoom = await _translationRoomRepository.GetByIdAsync(translationRoomId, ct);
            
            if (translationRoom == null)
                return Result.Failure<TranslationRoomDto>(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);

            return Result.Success(translationRoom.ToResponseDto());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while fetching translation room: {RoomId}", translationRoomId);
            return Result.Failure<TranslationRoomDto>("An unexpected error occurred while fetching the room.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<TranslationRoomListResponse>> GetTranslationRoomsAsync(GetTranslationRoomsRequest request, Guid userId, CancellationToken ct = default)
    {
        try
        {
            var page = Math.Max(1, request.Page);
            var pageSize = Math.Clamp(request.PageSize, 1, 100);
            var query = BuildAccessibleRoomsQuery(userId)
                .Where(r => r.DeletedAt == null && r.IsActive);

            query = ApplyRoomFilters(query, request);

            var total = query.Count();
            var roomEntities = query
                .OrderByDescending(r => r.StartedAt ?? r.ScheduledAt ?? r.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToList();

            var rooms = roomEntities.Select(r => ToListItemDto(r, userId)).ToList();

            return Result.Success(new TranslationRoomListResponse(rooms, total, page, pageSize));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while listing translation rooms for UserId: {UserId}", userId);
            return Result.Failure<TranslationRoomListResponse>("An unexpected error occurred while listing rooms.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<JoinTranslationRoomResponse>> JoinTranslationRoomAsync(JoinTranslationRoomRequest request, Guid userId, CancellationToken ct = default)
    {
        try
        {
            var translationRoom = await _translationRoomRepository.GetByCodeAsync(request.TranslationRoomCode, TranslationRoomConstants.TerminalStatuses, ct);
            if (translationRoom == null)
                return Result.Failure<JoinTranslationRoomResponse>(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);

            // WT-65: Fallback to user settings for Join
            var speakLang = request.SpeakLanguage;
            var listenLang = request.ListenLanguage;

            if (string.IsNullOrWhiteSpace(speakLang) || string.IsNullOrWhiteSpace(listenLang))
            {
                var userDefaults = await _unitOfWork.UserSettingsRepository.GetDefaultsAsync(userId, ct);
                if (userDefaults != null)
                {
                    speakLang ??= userDefaults.Value.DefaultSpeakLanguage;
                    listenLang ??= userDefaults.Value.DefaultListenLanguage;
                }
            }

            // WT-65: Validate Speak/Listen languages via Policy Engine
            string? validationError = await _languagePolicy.ValidateParticipantLanguagesAsync(speakLang, listenLang, translationRoom);

            // BR-006: Upsert participant record
            var participant = await _participantRepository.GetByRoomAndUserAsync(translationRoom.Id, userId, ct);

            // FR-1.4-007: Rejected participant language input MUST NOT be saved or applied to room participation state.
            if (validationError != null)
            {
                return Result.Failure<JoinTranslationRoomResponse>(validationError, ErrorCodes.ValidationError);
            }

            // BR-010: Block KICKED participants
            if (participant != null && participant.Status == nameof(TranslationRoomParticipantStatus.KICKED))
            {
                return Result.Failure<JoinTranslationRoomResponse>(TranslationRoomConstants.ErrorParticipantKicked, ErrorCodes.Forbidden);
            }

            // BR-011 & BR-012: Parse Settings
            bool requiresApproval = true;
            if (!string.IsNullOrEmpty(translationRoom.Settings))
            {
                var settings = System.Text.Json.JsonSerializer.Deserialize<TranslationRoomSettings>(translationRoom.Settings);
                requiresApproval = settings?.RequiresApproval ?? true;
            }

            var isHost = translationRoom.HostId == userId;

            if (participant == null)
            {
                participant = request.ToParticipantEntity(
                    translationRoom.Id, 
                    userId, 
                    speakLang!, 
                    listenLang!, 
                    requiresApproval,
                    isHost
                );
                
                await _participantRepository.AddAsync(participant, ct);
            }
            else
            {
                participant.UpdateFrom(
                    request, 
                    speakLang!, 
                    listenLang!, 
                    requiresApproval, 
                    isHost
                );
                
                _participantRepository.Update(participant);
            }

            await _unitOfWork.SaveChangesAsync(ct);

            // BR-008: Return comprehensive context
            return Result.Success(new JoinTranslationRoomResponse(
                translationRoom.ToResponseDto(),
                TranslationRoomParticipantMapper.ToDto(participant)
            ));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while joining translation room. UserId: {UserId}, RoomCode: {RoomCode}", userId, request.TranslationRoomCode);
            return Result.Failure<JoinTranslationRoomResponse>("An unexpected error occurred while joining the room.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> OpenWaitingRoomAsync(Guid translationRoomId, Guid hostId, CancellationToken ct = default)
    {
        try
        {
            var translationRoom = await _translationRoomRepository.GetByIdAsync(translationRoomId, ct);
            if (translationRoom == null) return Result.Failure(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);
            if (translationRoom.HostId != hostId) return Result.Failure(TranslationRoomConstants.ErrorUnauthorizedUpdateRoom, ErrorCodes.Unauthorized);
            
            if (translationRoom.Status != nameof(RoomStatus.SCHEDULED))
                return Result.Failure(TranslationRoomConstants.ErrorInvalidTransitionToWaiting, ErrorCodes.InvalidState);

            translationRoom.Status = nameof(RoomStatus.WAITING);
            translationRoom.UpdatedAt = DateTime.UtcNow;

            _translationRoomRepository.Update(translationRoom);
            await _unitOfWork.SaveChangesAsync(ct);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error opening waiting room. RoomId: {RoomId}", translationRoomId);
            return Result.Failure(TranslationRoomConstants.ErrorUnexpected, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<TranslationRoomDto>> StartTranslationRoomAsync(Guid translationRoomId, Guid hostId, CancellationToken ct = default)
    {
        try
        {
            var translationRoom = await _translationRoomRepository.GetByIdAsync(translationRoomId, ct);

            if (translationRoom == null)
                return Result.Failure<TranslationRoomDto>(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);

            if (translationRoom.HostId != hostId)
                return Result.Failure<TranslationRoomDto>("Only the host can start the room.", ErrorCodes.Forbidden);

            if (translationRoom.Status != nameof(RoomStatus.SCHEDULED) && translationRoom.Status != nameof(RoomStatus.WAITING))
                return Result.Failure<TranslationRoomDto>("Only scheduled or waiting rooms can be started.", ErrorCodes.InvalidState);

            translationRoom.Status = nameof(RoomStatus.IN_PROGRESS);
            translationRoom.StartedAt ??= DateTime.UtcNow;
            translationRoom.UpdatedAt = DateTime.UtcNow;
            translationRoom.UpdatedBy = hostId;

            _translationRoomRepository.Update(translationRoom);
            await _unitOfWork.SaveChangesAsync(ct);

            return Result.Success(translationRoom.ToResponseDto());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while starting translation room. RoomId: {RoomId}, HostId: {HostId}", translationRoomId, hostId);
            return Result.Failure<TranslationRoomDto>("An unexpected error occurred while starting the room.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> PauseTranslationRoomAsync(Guid translationRoomId, Guid hostId, CancellationToken ct = default)
    {
        try
        {
            var translationRoom = await _translationRoomRepository.GetByIdAsync(translationRoomId, ct);
            if (translationRoom == null) return Result.Failure(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);
            if (translationRoom.HostId != hostId) return Result.Failure(TranslationRoomConstants.ErrorUnauthorizedUpdateRoom, ErrorCodes.Unauthorized);
            
            if (translationRoom.Status != nameof(RoomStatus.IN_PROGRESS))
                return Result.Failure(TranslationRoomConstants.ErrorInvalidTransitionToPaused, ErrorCodes.InvalidState);

            translationRoom.Status = nameof(RoomStatus.PAUSED);
            translationRoom.UpdatedAt = DateTime.UtcNow;

            _translationRoomRepository.Update(translationRoom);
            await _unitOfWork.SaveChangesAsync(ct);

            // WT-67: Trigger Audio Routing State Machine to Pause
            await _audioRouteEventProcessor.ProcessEventAsync(translationRoomId, null, AudioRoutingEventType.room_pause.ToString(), "{}", ct);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error pausing translation room. RoomId: {RoomId}", translationRoomId);
            return Result.Failure(TranslationRoomConstants.ErrorUnexpected, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> ResumeTranslationRoomAsync(Guid translationRoomId, Guid hostId, CancellationToken ct = default)
    {
        try
        {
            var translationRoom = await _translationRoomRepository.GetByIdAsync(translationRoomId, ct);
            if (translationRoom == null) return Result.Failure(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);
            if (translationRoom.HostId != hostId) return Result.Failure(TranslationRoomConstants.ErrorUnauthorizedUpdateRoom, ErrorCodes.Unauthorized);
            
            if (translationRoom.Status != nameof(RoomStatus.PAUSED))
                return Result.Failure(TranslationRoomConstants.ErrorInvalidTransitionToInProgress, ErrorCodes.InvalidState);

            translationRoom.Status = nameof(RoomStatus.IN_PROGRESS);
            translationRoom.UpdatedAt = DateTime.UtcNow;

            _translationRoomRepository.Update(translationRoom);
            await _unitOfWork.SaveChangesAsync(ct);

            // WT-67: Trigger Audio Routing State Machine to Resume
            await _audioRouteEventProcessor.ProcessEventAsync(translationRoomId, null, AudioRoutingEventType.room_resume.ToString(), "{}", ct);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resuming translation room. RoomId: {RoomId}", translationRoomId);
            return Result.Failure(TranslationRoomConstants.ErrorUnexpected, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<TranslationRoomDto>> CancelTranslationRoomAsync(Guid translationRoomId, Guid hostId, CancellationToken ct = default)
    {
        try
        {
            var translationRoom = await _translationRoomRepository.GetByIdAsync(translationRoomId, ct);

            if (translationRoom == null)
                return Result.Failure<TranslationRoomDto>(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);

            if (translationRoom.HostId != hostId)
                return Result.Failure<TranslationRoomDto>("Only the host can cancel the room.", ErrorCodes.Forbidden);

            if (translationRoom.Status != nameof(RoomStatus.SCHEDULED) && translationRoom.Status != nameof(RoomStatus.WAITING))
                return Result.Failure<TranslationRoomDto>("Only scheduled or waiting rooms can be cancelled.", ErrorCodes.InvalidState);

            translationRoom.Status = nameof(RoomStatus.CANCELLED);
            translationRoom.EndedAt ??= DateTime.UtcNow;
            translationRoom.UpdatedAt = DateTime.UtcNow;
            translationRoom.UpdatedBy = hostId;

            _translationRoomRepository.Update(translationRoom);
            await _unitOfWork.SaveChangesAsync(ct);

            return Result.Success(translationRoom.ToResponseDto());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while cancelling translation room. RoomId: {RoomId}, HostId: {HostId}", translationRoomId, hostId);
            return Result.Failure<TranslationRoomDto>("An unexpected error occurred while cancelling the room.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> ExpireTranslationRoomAsync(Guid translationRoomId, CancellationToken ct = default)
    {
        try
        {
            var translationRoom = await _translationRoomRepository.GetByIdAsync(translationRoomId, ct);
            if (translationRoom == null) return Result.Failure(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);
            
            // Idempotent check
            if (translationRoom.Status == nameof(RoomStatus.EXPIRED))
                return Result.Success();

            if (translationRoom.Status != nameof(RoomStatus.SCHEDULED) && translationRoom.Status != nameof(RoomStatus.WAITING))
                return Result.Failure(TranslationRoomConstants.ErrorInvalidTransitionToExpired, ErrorCodes.InvalidState);

            translationRoom.Status = nameof(RoomStatus.EXPIRED);
            translationRoom.UpdatedAt = DateTime.UtcNow;

            _translationRoomRepository.Update(translationRoom);

            var participants = await _participantRepository.GetByRoomIdAsync(translationRoomId, ct);
            if (participants != null)
            {
                var participantsToUpdate = participants
                    .Where(p => p.Status == TranslationRoomParticipantStatus.CONNECTED.ToString() || 
                                p.Status == TranslationRoomParticipantStatus.WAITING.ToString())
                    .ToList();

                foreach (var participant in participantsToUpdate)
                {
                    participant.Status = TranslationRoomParticipantStatus.DISCONNECTED.ToString();
                    participant.UpdatedAt = DateTime.UtcNow;
                    _participantRepository.Update(participant);
                }
            }

            await _unitOfWork.SaveChangesAsync(ct);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error expiring translation room. RoomId: {RoomId}", translationRoomId);
            return Result.Failure(TranslationRoomConstants.ErrorUnexpected, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> EndTranslationRoomAsync(Guid translationRoomId, Guid hostId, CancellationToken ct = default)
    {
        try
        {
            var translationRoom = await _translationRoomRepository.GetByIdAsync(translationRoomId, ct);
            
            if (translationRoom == null)
                return Result.Failure(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);

            if (translationRoom.HostId != hostId)
                return Result.Failure(TranslationRoomConstants.ErrorUnauthorizedEndRoom, ErrorCodes.Unauthorized);

            if (translationRoom.Status == nameof(RoomStatus.ENDED))
                return Result.Success();

            if (translationRoom.Status != nameof(RoomStatus.IN_PROGRESS) && translationRoom.Status != nameof(RoomStatus.PAUSED))
                return Result.Failure(TranslationRoomConstants.ErrorInvalidTransitionToEnded, ErrorCodes.InvalidState);

            translationRoom.Status = nameof(RoomStatus.ENDED);
            translationRoom.EndedAt = DateTime.UtcNow;
            translationRoom.UpdatedAt = DateTime.UtcNow;

            if (translationRoom.StartedAt.HasValue)
            {
                translationRoom.DurationSeconds = (int)(translationRoom.EndedAt.Value - translationRoom.StartedAt.Value).TotalSeconds;
            }

            _translationRoomRepository.Update(translationRoom);

            var participants = await _participantRepository.GetByRoomIdAsync(translationRoomId, ct);
            if (participants != null)
            {
                var participantsToUpdate = participants
                    .Where(p => p.Status == TranslationRoomParticipantStatus.CONNECTED.ToString() || 
                                p.Status == TranslationRoomParticipantStatus.WAITING.ToString())
                    .ToList();

                foreach (var participant in participantsToUpdate)
                {
                    participant.Status = TranslationRoomParticipantStatus.DISCONNECTED.ToString();
                    participant.UpdatedAt = DateTime.UtcNow;
                    _participantRepository.Update(participant);
                }
            }

            await _unitOfWork.SaveChangesAsync(ct);

            // WT-67: Trigger Audio Routing State Machine
            await _audioRouteEventProcessor.ProcessEventAsync(translationRoomId, null, AudioRoutingEventType.session_ends.ToString(), "{}", ct);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while ending translation room. RoomId: {RoomId}, HostId: {HostId}", translationRoomId, hostId);
            return Result.Failure(TranslationRoomConstants.ErrorUnexpectedEndRoom, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> UpdateTranslationRoomSettingsAsync(Guid translationRoomId, Guid hostId, UpdateRoomSettingsRequest request, CancellationToken ct = default)
    {
        try
        {
            var translationRoom = await _translationRoomRepository.GetByIdAsync(translationRoomId, ct);
            
            if (translationRoom == null)
                return Result.Failure(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);

            if (translationRoom.HostId != hostId)
                return Result.Failure(TranslationRoomConstants.ErrorUnauthorizedUpdateRoom, ErrorCodes.Unauthorized);

            if (translationRoom.Status != nameof(RoomStatus.SCHEDULED) && translationRoom.Status != nameof(RoomStatus.WAITING))
                return Result.Failure(TranslationRoomConstants.ErrorSettingsLocked, ErrorCodes.InvalidState);

            // WT-65: Update and Validate Source Language
            if (!string.IsNullOrWhiteSpace(request.SourceLanguage))
            {
                if (!await _languagePolicy.IsSupportedAsync(request.SourceLanguage))
                    return Result.Failure(TranslationRoomConstants.ValidationSourceLanguageUnsupported, ErrorCodes.ValidationError);
                
                translationRoom.SourceLanguage = request.SourceLanguage;
            }

            // WT-65: Update and Validate Target Languages
            if (request.TargetLanguages != null && request.TargetLanguages.Count > 0)
            {
                foreach (var lang in request.TargetLanguages)
                {
                    if (!await _languagePolicy.IsSupportedAsync(lang))
                        return Result.Failure(string.Format(TranslationRoomConstants.ValidationLanguageUnsupported, lang), ErrorCodes.ValidationError);
                }
                
                translationRoom.TargetLanguages = LanguageHelper.SerializeTargetLanguages(request.TargetLanguages);
            }

            // Update Settings (RequiresApproval)
            if (request.Settings != null)
            {
                var newSettings = new TranslationRoomSettings 
                { 
                    RequiresApproval = request.Settings.RequiresApproval,
                    ArtifactAccess = request.Settings.ArtifactAccess
                };
                translationRoom.Settings = System.Text.Json.JsonSerializer.Serialize(newSettings);
            }

            translationRoom.UpdatedAt = DateTime.UtcNow;

            _translationRoomRepository.Update(translationRoom);
            await _unitOfWork.SaveChangesAsync(ct);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while updating translation room settings. RoomId: {RoomId}, HostId: {HostId}", translationRoomId, hostId);
            return Result.Failure(TranslationRoomConstants.ErrorUnexpectedUpdateRoomSettings, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<TranslationRoomHistoryResponse>> GetTranslationRoomHistoryAsync(GetTranslationRoomsRequest request, Guid userId, CancellationToken ct = default)
    {
        try
        {
            var page = Math.Max(1, request.Page);
            var pageSize = Math.Clamp(request.PageSize, 1, 100);
            var historyRequest = request with { Status = request.Status ?? $"{nameof(RoomStatus.ENDED)},{nameof(RoomStatus.CANCELLED)}" };
            var query = ApplyRoomFilters(BuildAccessibleRoomsQuery(userId), historyRequest)
                .Where(r => r.DeletedAt == null && r.IsActive);

            var total = query.Count();
            
            var roomEntities = query
                .OrderByDescending(r => r.EndedAt ?? r.StartedAt ?? r.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToList();

            var roomIds = roomEntities.Select(r => r.Id).ToList();

            var participantsByRoom = _unitOfWork.Repository<TranslationRoomParticipant>()
                .Query()
                .Where(p => roomIds.Contains(p.TranslationRoomId))
                .ToList()
                .GroupBy(p => p.TranslationRoomId)
                .ToDictionary(g => g.Key, g => g.Select(p => p.ToDto()).ToList());

            var artifactsByRoom = _unitOfWork.Repository<TranslationRoomArtifact>()
                .Query()
                .Where(a => roomIds.Contains(a.TranslationRoomId) && a.DeletedAt == null)
                .OrderByDescending(a => a.CreatedAt)
                .ToList()
                .GroupBy(a => a.TranslationRoomId)
                .ToDictionary(g => g.Key, g => g.Select(ToArtifactDto).ToList());

            var rooms = roomEntities.Select(room => new TranslationRoomHistoryItemDto(
                    ToListItemDto(room, userId),
                    participantsByRoom.GetValueOrDefault(room.Id, new List<TranslationRoomParticipantDto>()),
                    artifactsByRoom.GetValueOrDefault(room.Id, new List<TranslationRoomArtifactDto>())
                ))
                .ToList();

            return Result.Success(new TranslationRoomHistoryResponse(rooms, total, page, pageSize));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while loading translation room history for UserId: {UserId}", userId);
            return Result.Failure<TranslationRoomHistoryResponse>("An unexpected error occurred while loading room history.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<List<TranslationRoomArtifactDto>>> GetTranslationRoomArtifactsAsync(Guid translationRoomId, Guid userId, CancellationToken ct = default)
    {
        try
        {
            if (!await CanAccessRoomAsync(translationRoomId, userId, ct))
                return Result.Failure<List<TranslationRoomArtifactDto>>(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);

            var artifacts = _unitOfWork.Repository<TranslationRoomArtifact>()
                .Query()
                .Where(a => a.TranslationRoomId == translationRoomId && a.DeletedAt == null)
                .OrderByDescending(a => a.CreatedAt)
                .Select(a => ToArtifactDto(a))
                .ToList();

            return Result.Success(artifacts);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while loading artifacts. RoomId: {RoomId}, UserId: {UserId}", translationRoomId, userId);
            return Result.Failure<List<TranslationRoomArtifactDto>>("An unexpected error occurred while loading artifacts.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<TranslationRoomFeedbackStateDto>> GetFeedbackStateAsync(Guid translationRoomId, Guid userId, CancellationToken ct = default)
    {
        try
        {
            var room = await _translationRoomRepository.GetByIdAsync(translationRoomId, ct);
            if (room == null || !await CanAccessRoomAsync(translationRoomId, userId, ct))
                return Result.Failure<TranslationRoomFeedbackStateDto>(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);

            if (room.Status != nameof(RoomStatus.ENDED))
                return Result.Failure<TranslationRoomFeedbackStateDto>("Feedback is only available after a room ends.", ErrorCodes.InvalidState);

            var feedback = await _unitOfWork.Repository<TranslationRoomFeedback>()
                .FirstOrDefaultAsync(f => f.TranslationRoomId == translationRoomId && f.UserId == userId, ct: ct);

            return Result.Success(new TranslationRoomFeedbackStateDto(feedback != null, feedback != null ? ToFeedbackDto(feedback) : null));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while loading feedback state. RoomId: {RoomId}, UserId: {UserId}", translationRoomId, userId);
            return Result.Failure<TranslationRoomFeedbackStateDto>("An unexpected error occurred while loading feedback.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<TranslationRoomFeedbackDto>> SubmitFeedbackAsync(Guid translationRoomId, Guid userId, SubmitTranslationRoomFeedbackRequest request, CancellationToken ct = default)
    {
        try
        {
            var room = await _translationRoomRepository.GetByIdAsync(translationRoomId, ct);
            if (room == null || !await CanAccessRoomAsync(translationRoomId, userId, ct))
                return Result.Failure<TranslationRoomFeedbackDto>(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);

            if (room.Status != nameof(RoomStatus.ENDED))
                return Result.Failure<TranslationRoomFeedbackDto>("Feedback is only available after a room ends.", ErrorCodes.InvalidState);

            var feedbackRepository = _unitOfWork.Repository<TranslationRoomFeedback>();
            var existing = await feedbackRepository.FirstOrDefaultAsync(f => f.TranslationRoomId == translationRoomId && f.UserId == userId, ct: ct);
            if (existing != null)
                return Result.Failure<TranslationRoomFeedbackDto>("Feedback has already been submitted for this room.", ErrorCodes.InvalidState);

            var feedback = new TranslationRoomFeedback
            {
                Id = Guid.CreateVersion7(),
                TranslationRoomId = translationRoomId,
                UserId = userId,
                OverallRating = request.OverallRating,
                TranslationQuality = request.TranslationQuality,
                AudioQuality = request.AudioQuality,
                VoiceCloneQuality = request.VoiceCloneQuality,
                AiSummaryQuality = request.AiSummaryQuality,
                Comments = string.IsNullOrWhiteSpace(request.Comments) ? null : request.Comments.Trim(),
                CommunicationInsights = request.CommunicationInsights == null ? null : JsonSerializer.Serialize(request.CommunicationInsights),
                CreatedAt = DateTime.UtcNow
            };

            await feedbackRepository.AddAsync(feedback, ct);
            await _unitOfWork.SaveChangesAsync(ct);

            return Result.Success(ToFeedbackDto(feedback));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while submitting feedback. RoomId: {RoomId}, UserId: {UserId}", translationRoomId, userId);
            return Result.Failure<TranslationRoomFeedbackDto>("An unexpected error occurred while submitting feedback.", ErrorCodes.InternalServerError);
        }
    }

    private IQueryable<TranslationRoom> BuildAccessibleRoomsQuery(Guid userId)
    {
        return _unitOfWork.Repository<TranslationRoom>()
            .Query()
            .Where(r => r.HostId == userId || r.TranslationRoomParticipants.Any(p => p.UserId == userId));
    }

    private static IQueryable<TranslationRoom> ApplyRoomFilters(IQueryable<TranslationRoom> query, GetTranslationRoomsRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Status))
        {
            var statuses = request.Status
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => s.ToUpperInvariant())
                .ToList();
            query = query.Where(r => statuses.Contains(r.Status.ToUpper()));
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var search = request.Search.Trim().ToLowerInvariant();
            query = query.Where(r =>
                r.Title.ToLower().Contains(search) ||
                r.TranslationRoomCode.ToLower().Contains(search) ||
                (r.Description != null && r.Description.ToLower().Contains(search)));
        }

        if (request.From.HasValue)
            query = query.Where(r => (r.ScheduledAt ?? r.StartedAt ?? r.CreatedAt) >= request.From.Value);

        if (request.To.HasValue)
            query = query.Where(r => (r.ScheduledAt ?? r.StartedAt ?? r.CreatedAt) <= request.To.Value);

        return query;
    }

    private Task<bool> CanAccessRoomAsync(Guid translationRoomId, Guid userId, CancellationToken ct)
    {
        return Task.FromResult(_unitOfWork.Repository<TranslationRoom>()
            .Query()
            .Where(r => r.Id == translationRoomId && r.DeletedAt == null && r.IsActive)
            .Any(r => r.HostId == userId || r.TranslationRoomParticipants.Any(p => p.UserId == userId)));
    }

    private static TranslationRoomListItemDto ToListItemDto(TranslationRoom room, Guid userId)
    {
        var settings = !string.IsNullOrEmpty(room.Settings)
            ? JsonSerializer.Deserialize<RoomSettingsResponse>(room.Settings)
            : new RoomSettingsResponse(true, WarpTalk.TranslationRoomService.Domain.Enums.ArtifactAccessLevel.HostOnly);

        return new TranslationRoomListItemDto(
            room.Id,
            room.WorkspaceId,
            room.HostId,
            room.Title,
            room.Description,
            room.TranslationRoomCode,
            room.Status,
            Enum.Parse<TranslationRoomType>(room.TranslationRoomType, true),
            room.MaxParticipants,
            room.SourceLanguage,
            LanguageHelper.ParseTargetLanguages(room.TargetLanguages),
            room.ScheduledAt,
            room.StartedAt,
            room.EndedAt,
            room.DurationSeconds,
            room.CreatedAt,
            settings ?? new RoomSettingsResponse(true, WarpTalk.TranslationRoomService.Domain.Enums.ArtifactAccessLevel.HostOnly),
            room.TranslationRoomParticipants.Count,
            room.HostId == userId
        );
    }

    private static TranslationRoomArtifactDto ToArtifactDto(TranslationRoomArtifact artifact)
    {
        var type = artifact.FileFormat?.Equals("debug", StringComparison.OrdinalIgnoreCase) == true
            ? "DEBUG_LOG"
            : "TRANSCRIPT_EXPORT";

        return new TranslationRoomArtifactDto(
            artifact.Id,
            artifact.TranslationRoomId,
            type,
            BuildArtifactTitle(type, artifact.FileFormat),
            artifact.FileUrl,
            artifact.FileFormat,
            artifact.FileSizeBytes,
            artifact.ContainsRawAudio,
            artifact.ContainsRawVideo,
            artifact.ConsentRequired,
            artifact.RetentionUntil,
            artifact.Status,
            artifact.CreatedAt
        );
    }

    private static string BuildArtifactTitle(string type, string? format)
    {
        var label = type.ToLowerInvariant().Replace('_', ' ');
        return string.IsNullOrWhiteSpace(format) ? label : $"{label} ({format.ToUpperInvariant()})";
    }

    private static TranslationRoomFeedbackDto ToFeedbackDto(TranslationRoomFeedback feedback)
    {
        Dictionary<string, object>? insights = null;
        if (!string.IsNullOrWhiteSpace(feedback.CommunicationInsights))
        {
            try
            {
                insights = JsonSerializer.Deserialize<Dictionary<string, object>>(feedback.CommunicationInsights);
            }
            catch
            {
                insights = new Dictionary<string, object> { ["raw"] = feedback.CommunicationInsights };
            }
        }

        return new TranslationRoomFeedbackDto(
            feedback.Id,
            feedback.TranslationRoomId,
            feedback.UserId,
            feedback.OverallRating,
            feedback.TranslationQuality,
            feedback.AudioQuality,
            feedback.VoiceCloneQuality,
            feedback.AiSummaryQuality,
            feedback.Comments,
            insights,
            feedback.CreatedAt
        );
    }
}