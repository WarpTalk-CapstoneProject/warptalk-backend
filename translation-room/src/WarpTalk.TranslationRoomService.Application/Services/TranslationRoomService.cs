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
    private readonly IAudioRouteEventProcessorService _audioRouteEventProcessor;
    private readonly ILogger<TranslationRoomService> _logger;

    public TranslationRoomService(IUnitOfWork unitOfWork, ILanguagePolicy languagePolicy, IAudioRouteEventProcessorService audioRouteEventProcessor, ILogger<TranslationRoomService> logger)
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
            var status = request.ScheduledAt.HasValue ? RoomStatus.SCHEDULED : RoomStatus.WAITING;

            // 2. Generate unique 12-char alphanumeric TranslationRoomCode
            string roomCode;
            bool exists;
            do
            {
                roomCode = RoomCodeGenerator.GenerateCode();
                exists = await _translationRoomRepository.ExistsByCodeAsync(roomCode, TranslationRoomConstants.TerminalStatuses, ct);
            } while (exists);

            // 3. Create entity
            var room = TranslationRoomMapper.ToEntity(request, hostId, roomCode, status, sourceLang, targetLangs);

            // 4. Save via repository and UnitOfWork
            await _translationRoomRepository.AddAsync(room, ct);
            await _unitOfWork.SaveChangesAsync(ct);

            // 5. Return mapped response
            var settingsResponse = request.Settings != null 
                ? new RoomSettingsResponse(request.Settings.RequiresApproval, request.Settings.HistoryAccess) 
                : new RoomSettingsResponse(true, ArtifactAccessLevel.HostOnly);

            return Result.Success(TranslationRoomMapper.ToResponseDto(room, settingsResponse));
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

            var settings = !string.IsNullOrEmpty(translationRoom.Settings) 
                ? JsonSerializer.Deserialize<RoomSettingsResponse>(translationRoom.Settings) 
                : new RoomSettingsResponse(true, ArtifactAccessLevel.HostOnly);

            return Result.Success(TranslationRoomMapper.ToResponseDto(translationRoom, settings!));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while fetching translation room: {RoomId}", translationRoomId);
            return Result.Failure<TranslationRoomDto>("An unexpected error occurred while fetching the room.", ErrorCodes.InternalServerError);
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
            if (participant != null && participant.Status == TranslationRoomParticipantStatus.KICKED)
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
                participant = TranslationRoomParticipantMapper.ToParticipantEntity(
                    translationRoom.Id, 
                    userId, 
                    request, 
                    speakLang!, 
                    listenLang!, 
                    requiresApproval,
                    isHost
                );
                
                await _participantRepository.AddAsync(participant, ct);
            }
            else
            {
                TranslationRoomParticipantMapper.UpdateParticipantEntity(
                    participant, 
                    request, 
                    speakLang!, 
                    listenLang!, 
                    requiresApproval, 
                    isHost
                );
                
                _participantRepository.Update(participant);
            }

            await _unitOfWork.SaveChangesAsync(ct);

            var settingsResponse = !string.IsNullOrEmpty(translationRoom.Settings) 
                ? JsonSerializer.Deserialize<RoomSettingsResponse>(translationRoom.Settings) 
                : new RoomSettingsResponse(true, ArtifactAccessLevel.HostOnly);

            // BR-008: Return comprehensive context
            return Result.Success(new JoinTranslationRoomResponse(
                TranslationRoomMapper.ToResponseDto(translationRoom, settingsResponse!),
                TranslationRoomParticipantMapper.ToParticipantDto(participant)
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
            
            if (translationRoom.Status != RoomStatus.SCHEDULED)
                return Result.Failure(TranslationRoomConstants.ErrorInvalidTransitionToWaiting, ErrorCodes.InvalidState);

            translationRoom.Status = RoomStatus.WAITING;
            translationRoom.UpdatedAt = DateTime.UtcNow;

            _translationRoomRepository.Update(translationRoom);
            await _unitOfWork.SaveChangesAsync(ct);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error opening waiting room. RoomId: {RoomId}", translationRoomId);
            return Result.Failure("An unexpected error occurred.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> StartTranslationRoomAsync(Guid translationRoomId, Guid hostId, CancellationToken ct = default)
    {
        try
        {
            var translationRoom = await _translationRoomRepository.GetByIdAsync(translationRoomId, ct);
            if (translationRoom == null) return Result.Failure(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);
            if (translationRoom.HostId != hostId) return Result.Failure(TranslationRoomConstants.ErrorUnauthorizedUpdateRoom, ErrorCodes.Unauthorized);
            
            if (translationRoom.Status != RoomStatus.WAITING && translationRoom.Status != RoomStatus.PAUSED)
                return Result.Failure(TranslationRoomConstants.ErrorInvalidTransitionToInProgress, ErrorCodes.InvalidState);

            translationRoom.Status = RoomStatus.IN_PROGRESS;
            
            if (!translationRoom.StartedAt.HasValue)
                translationRoom.StartedAt = DateTime.UtcNow;
                
            translationRoom.UpdatedAt = DateTime.UtcNow;

            _translationRoomRepository.Update(translationRoom);
            await _unitOfWork.SaveChangesAsync(ct);

            // WT-67: Trigger Audio Routing State Machine
            await _audioRouteEventProcessor.ProcessEventAsync(translationRoomId, null, AudioRoutingEventType.session_starts.ToString(), "{}", ct);
            await _audioRouteEventProcessor.ProcessEventAsync(translationRoomId, null, AudioRoutingEventType.participants_and_languages_configured.ToString(), "{}", ct);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting translation room. RoomId: {RoomId}", translationRoomId);
            return Result.Failure("An unexpected error occurred.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> PauseTranslationRoomAsync(Guid translationRoomId, Guid hostId, CancellationToken ct = default)
    {
        try
        {
            var translationRoom = await _translationRoomRepository.GetByIdAsync(translationRoomId, ct);
            if (translationRoom == null) return Result.Failure(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);
            if (translationRoom.HostId != hostId) return Result.Failure(TranslationRoomConstants.ErrorUnauthorizedUpdateRoom, ErrorCodes.Unauthorized);
            
            if (translationRoom.Status != RoomStatus.IN_PROGRESS)
                return Result.Failure(TranslationRoomConstants.ErrorInvalidTransitionToPaused, ErrorCodes.InvalidState);

            translationRoom.Status = RoomStatus.PAUSED;
            translationRoom.UpdatedAt = DateTime.UtcNow;

            _translationRoomRepository.Update(translationRoom);
            await _unitOfWork.SaveChangesAsync(ct);

            // WT-67: Trigger Audio Routing State Machine to Pause
            await _audioRouteEventProcessor.ProcessEventAsync(translationRoomId, null, AudioRoutingEventType.host_pauses_session.ToString(), "{}", ct);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error pausing translation room. RoomId: {RoomId}", translationRoomId);
            return Result.Failure("An unexpected error occurred.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> ResumeTranslationRoomAsync(Guid translationRoomId, Guid hostId, CancellationToken ct = default)
    {
        try
        {
            var translationRoom = await _translationRoomRepository.GetByIdAsync(translationRoomId, ct);
            if (translationRoom == null) return Result.Failure(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);
            if (translationRoom.HostId != hostId) return Result.Failure(TranslationRoomConstants.ErrorUnauthorizedUpdateRoom, ErrorCodes.Unauthorized);
            
            if (translationRoom.Status != RoomStatus.PAUSED)
                return Result.Failure(TranslationRoomConstants.ErrorInvalidTransitionToInProgress, ErrorCodes.InvalidState);

            translationRoom.Status = RoomStatus.IN_PROGRESS;
            translationRoom.UpdatedAt = DateTime.UtcNow;

            _translationRoomRepository.Update(translationRoom);
            await _unitOfWork.SaveChangesAsync(ct);

            // WT-67: Trigger Audio Routing State Machine to Resume
            await _audioRouteEventProcessor.ProcessEventAsync(translationRoomId, null, AudioRoutingEventType.host_resumes_session.ToString(), "{}", ct);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resuming translation room. RoomId: {RoomId}", translationRoomId);
            return Result.Failure("An unexpected error occurred.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> CancelTranslationRoomAsync(Guid translationRoomId, Guid hostId, CancellationToken ct = default)
    {
        try
        {
            var translationRoom = await _translationRoomRepository.GetByIdAsync(translationRoomId, ct);
            if (translationRoom == null) return Result.Failure(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);
            if (translationRoom.HostId != hostId) return Result.Failure(TranslationRoomConstants.ErrorUnauthorizedUpdateRoom, ErrorCodes.Unauthorized);
            
            if (translationRoom.Status != RoomStatus.SCHEDULED && translationRoom.Status != RoomStatus.WAITING)
                return Result.Failure(TranslationRoomConstants.ErrorInvalidTransitionToCancelled, ErrorCodes.InvalidState);

            translationRoom.Status = RoomStatus.CANCELLED;
            translationRoom.UpdatedAt = DateTime.UtcNow;

            _translationRoomRepository.Update(translationRoom);
            await _unitOfWork.SaveChangesAsync(ct);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cancelling translation room. RoomId: {RoomId}", translationRoomId);
            return Result.Failure("An unexpected error occurred.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> ExpireTranslationRoomAsync(Guid translationRoomId, CancellationToken ct = default)
    {
        try
        {
            var translationRoom = await _translationRoomRepository.GetByIdAsync(translationRoomId, ct);
            if (translationRoom == null) return Result.Failure(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);
            
            // Idempotent check
            if (translationRoom.Status == RoomStatus.EXPIRED)
                return Result.Success();

            if (translationRoom.Status != RoomStatus.SCHEDULED && translationRoom.Status != RoomStatus.WAITING)
                return Result.Failure(TranslationRoomConstants.ErrorInvalidTransitionToExpired, ErrorCodes.InvalidState);

            translationRoom.Status = RoomStatus.EXPIRED;
            translationRoom.UpdatedAt = DateTime.UtcNow;

            _translationRoomRepository.Update(translationRoom);
            await _unitOfWork.SaveChangesAsync(ct);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error expiring translation room. RoomId: {RoomId}", translationRoomId);
            return Result.Failure("An unexpected error occurred.", ErrorCodes.InternalServerError);
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

            if (translationRoom.Status != RoomStatus.IN_PROGRESS && translationRoom.Status != RoomStatus.PAUSED)
                return Result.Failure(TranslationRoomConstants.ErrorInvalidTransitionToEnded, ErrorCodes.InvalidState);

            translationRoom.Status = RoomStatus.ENDED;
            translationRoom.EndedAt = DateTime.UtcNow;
            translationRoom.UpdatedAt = DateTime.UtcNow;

            if (translationRoom.StartedAt.HasValue)
            {
                translationRoom.DurationSeconds = (int)(translationRoom.EndedAt.Value - translationRoom.StartedAt.Value).TotalSeconds;
            }

            _translationRoomRepository.Update(translationRoom);
            await _unitOfWork.SaveChangesAsync(ct);

            // WT-67: Trigger Audio Routing State Machine
            await _audioRouteEventProcessor.ProcessEventAsync(translationRoomId, null, AudioRoutingEventType.host_ends_session.ToString(), "{}", ct);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while ending translation room. RoomId: {RoomId}, HostId: {HostId}", translationRoomId, hostId);
            return Result.Failure("An unexpected error occurred while ending the room.", ErrorCodes.InternalServerError);
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

            if (translationRoom.Status != RoomStatus.SCHEDULED && translationRoom.Status != RoomStatus.WAITING)
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
                    RequiresApproval = request.Settings.RequiresApproval 
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
            return Result.Failure("An unexpected error occurred while updating the room settings.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<List<TranslationRoomDto>>> GetRoomHistoryAsync(Guid userId, int limit = 50, int offset = 0, CancellationToken ct = default)
    {
        try
        {
            var userRooms = await _translationRoomRepository.GetHistoryByUserIdAsync(userId, limit, offset, ct);
                
            var visibleRooms = new List<TranslationRoom>();
            foreach(var room in userRooms)
            {
                if (room.HostId == userId)
                {
                    visibleRooms.Add(room);
                    continue;
                }
                
                var settings = !string.IsNullOrEmpty(room.Settings) ? JsonSerializer.Deserialize<TranslationRoomSettings>(room.Settings) : null;
                if (settings?.HistoryAccess == ArtifactAccessLevel.Participants || settings?.HistoryAccess == ArtifactAccessLevel.Workspace)
                {
                    visibleRooms.Add(room);
                }
            }

            var dtos = visibleRooms.Select(room =>
            {
                var settings = !string.IsNullOrEmpty(room.Settings) 
                    ? JsonSerializer.Deserialize<RoomSettingsResponse>(room.Settings) 
                    : new RoomSettingsResponse(true, ArtifactAccessLevel.HostOnly);

                var artifacts = room.TranslationRoomArtifacts?.Select(ArtifactMapper.ToArtifactDto).ToList() ?? new List<RoomArtifactDto>();

                return TranslationRoomMapper.ToHistoryDto(room, settings!, artifacts);
            }).ToList();
            
            return Result<List<TranslationRoomDto>>.Success(dtos);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting room history for user {UserId}", userId);
            return Result.Failure<List<TranslationRoomDto>>("An unexpected error occurred.", ErrorCodes.InternalServerError);
        }
    }
}
