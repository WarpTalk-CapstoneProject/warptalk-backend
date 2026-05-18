using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.Mappers;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.Interfaces;

namespace WarpTalk.TranslationRoomService.Application.Services;

public class TranslationRoomAudioRouteService : ITranslationRoomAudioRouteService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITranslationRoomRepository _translationRoomRepository;
    private readonly ITranslationRoomParticipantRepository _translationRoomParticipantRepository;
    private readonly ITranslationRoomAudioRouteRepository _translationRoomAudioRouteRepository;
    private readonly IConnectionMultiplexer _redisConnection;
    private readonly IAudioRouteEventProcessorService _eventProcessor;
    private readonly ILogger<TranslationRoomAudioRouteService> _logger;

    public TranslationRoomAudioRouteService(
        IUnitOfWork unitOfWork, 
        IConnectionMultiplexer redisConnection,
        IAudioRouteEventProcessorService eventProcessor,
        ILogger<TranslationRoomAudioRouteService> logger)
    {
        _unitOfWork = unitOfWork;
        _translationRoomRepository = _unitOfWork.TranslationRoomRepository;
        _translationRoomParticipantRepository = _unitOfWork.TranslationRoomParticipantRepository;
        _translationRoomAudioRouteRepository = _unitOfWork.TranslationRoomAudioRouteRepository;
        _redisConnection = redisConnection;
        _eventProcessor = eventProcessor;
        _logger = logger;
    }

    public async Task<Result<List<TranslationRoomAudioRouteDto>>> GenerateRoutesAsync(Guid roomId, CancellationToken ct = default)
    {
        try
        {
            var room = await _translationRoomRepository.GetByIdAsync(roomId, ct);
            if (room == null)
            {
                return Result.Failure<List<TranslationRoomAudioRouteDto>>(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);
            }

            // Guard clause: Room lacking data/policy
            if (string.IsNullOrWhiteSpace(room.SourceLanguage) || string.IsNullOrWhiteSpace(room.TargetLanguages))
            {
                return Result.Failure<List<TranslationRoomAudioRouteDto>>(AudioRouteConstants.ErrorRoomPolicyIncomplete, ErrorCodes.InvalidState);
            }

            var participants = await _translationRoomParticipantRepository.GetByRoomIdAsync(roomId, ct);
            
            // Only consider participants that are CONNECTED or in a state where they can speak/listen
            var activeParticipants = participants
                .Where(p => p.Status == TranslationRoomParticipantStatus.CONNECTED)
                .ToList();

            // Guard clause: Not enough participants
            if (activeParticipants.Count < 2)
            {
                // Can't create routes with < 2 participants, return empty or failure
                // We'll return empty list as it's a valid state, just no routes yet.
                return Result.Success(new List<TranslationRoomAudioRouteDto>());
            }

            var existingRoutes = await _translationRoomAudioRouteRepository.GetRoutesByRoomIdAsync(roomId, ct);
            var desiredRoutes = new List<TranslationRoomAudioRoute>();

            foreach (var pSrc in activeParticipants)
            {
                foreach (var pTgt in activeParticipants)
                {
                    // Guard clause: Prevent self-route
                    if (pSrc.Id == pTgt.Id)
                        continue; 

                    // Guard clause: Invalid participant language
                    if (string.IsNullOrWhiteSpace(pSrc.SpeakLanguage) || string.IsNullOrWhiteSpace(pTgt.ListenLanguage))
                        continue;

                    var route = TranslationRoomAudioRouteMapper.ToEntity(roomId, pSrc, pTgt);
                    desiredRoutes.Add(route);
                }
            }

            var routesToAdd = new List<TranslationRoomAudioRoute>();
            var routesToUpdate = new List<TranslationRoomAudioRoute>();

            var existingRoutesMap = existingRoutes.ToDictionary(
                r => (r.SourceParticipantId, r.TargetParticipantId, r.SourceLanguage, r.TargetLanguage));

            foreach (var desired in desiredRoutes)
            {
                var key = (desired.SourceParticipantId, desired.TargetParticipantId, desired.SourceLanguage, desired.TargetLanguage);
                if (existingRoutesMap.TryGetValue(key, out var existing))
                {
                    // Existing route still valid. 
                    // If it was COMPLETED or STOPPING, it means participant rejoined or changed back language. We reset to IDLE.
                    if (existing.Status == AudioRouteStatus.COMPLETED.ToString() || existing.Status == AudioRouteStatus.STOPPING.ToString())
                    {
                        existing.Status = AudioRouteStatus.IDLE.ToString();
                        existing.StreamId = null;
                        existing.UpdatedAt = DateTime.UtcNow;
                        routesToUpdate.Add(existing);
                    }
                    // Keep intact if it is in an active or degraded state.
                    
                    existingRoutesMap.Remove(key);
                }
                else
                {
                    // New route
                    routesToAdd.Add(desired);
                }
            }

            // Any routes left in existingRoutesMap are no longer valid (participants left, changed language, etc.)
            foreach (var obsolete in existingRoutesMap.Values)
            {
                if (obsolete.Status != AudioRouteStatus.COMPLETED.ToString() && obsolete.Status != AudioRouteStatus.STOPPING.ToString())
                {
                    obsolete.Status = AudioRouteStatus.STOPPING.ToString();
                    obsolete.UpdatedAt = DateTime.UtcNow;
                    routesToUpdate.Add(obsolete);
                }
            }

            if (routesToAdd.Any())
            {
                await _translationRoomAudioRouteRepository.AddRoutesAsync(routesToAdd, ct);
            }

            if (routesToUpdate.Any())
            {
                await _translationRoomAudioRouteRepository.UpdateRoutesAsync(routesToUpdate, ct);
            }

            await _unitOfWork.SaveChangesAsync(ct);

            var transitionResult = await _eventProcessor.ProcessEventAsync(
                roomId, 
                null, 
                AudioRoutingEventType.config_ready.ToString(), 
                "{}", 
                ct);

            if (!transitionResult.IsSuccess)
            {
                _logger.LogError("Failed to transition generated routes to ROUTING_READY for room {RoomId}. Error: {Error}", roomId, transitionResult.Error);
                return Result.Failure<List<TranslationRoomAudioRouteDto>>(transitionResult.Error ?? "Failed to transition generated routes to ROUTING_READY", transitionResult.ErrorCode);
            }

            var allRoutes = await _translationRoomAudioRouteRepository.GetRoutesByRoomIdAsync(roomId, ct);
            var activeOrPendingRoutes = allRoutes
                .Where(r => r.Status != AudioRouteStatus.COMPLETED.ToString())
                .Select(TranslationRoomAudioRouteMapper.ToDto)
                .ToList();

            return Result.Success(activeOrPendingRoutes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while generating routes for room {RoomId}", roomId);
            return Result.Failure<List<TranslationRoomAudioRouteDto>>(AudioRouteConstants.ErrorGenerateAudioRoutesUnexpected, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<List<TranslationRoomAudioRouteDto>>> GetRoutesAsync(Guid roomId, CancellationToken ct = default)
    {
        try
        {
            var routes = await _translationRoomAudioRouteRepository.GetRoutesByRoomIdAsync(roomId, ct);

            var dtos = routes.Select(TranslationRoomAudioRouteMapper.ToDto).ToList();

            return Result.Success(dtos);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while fetching routes for room {RoomId}", roomId);
            return Result.Failure<List<TranslationRoomAudioRouteDto>>(AudioRouteConstants.ErrorFetchAudioRoutesUnexpected, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<TranslationRoomAudioRouteDto>> UpdateRuntimeContextAsync(Guid roomId, Guid routeId, UpdateAudioRouteRuntimeContextDto dto, CancellationToken ct = default)
    {
        try
        {
            
            var route = await _translationRoomAudioRouteRepository.GetByIdAsync(routeId, ct);
            if (route == null)
            {
                return Result.Failure<TranslationRoomAudioRouteDto>(AudioRouteConstants.ErrorRouteNotFound, ErrorCodes.NotFound);
            }

            if (route.TranslationRoomId != roomId)
            {
                return Result.Failure<TranslationRoomAudioRouteDto>(AudioRouteConstants.ErrorRouteNotBelongToRoom, ErrorCodes.ValidationError);
            }

            if (route.Status == AudioRouteStatus.COMPLETED.ToString())
            {
                return Result.Failure<TranslationRoomAudioRouteDto>(AudioRouteConstants.ErrorCannotUpdateCompletedRoute, ErrorCodes.InvalidState);
            }

            bool updated = false;

            if (dto.StreamId != null && route.StreamId != dto.StreamId)
            {
                route.StreamId = dto.StreamId;
                updated = true;
            }

            if (dto.Status.HasValue && route.Status != dto.Status.Value.ToString())
            {
                route.Status = dto.Status.Value.ToString();
                if (dto.Status.Value == AudioRouteStatus.AUDIO_ROUTING_ACTIVE)
                {
                    route.StartedAt = DateTime.UtcNow;
                }
                updated = true;
            }

            if (updated)
            {
                route.UpdatedAt = DateTime.UtcNow;
                _translationRoomAudioRouteRepository.Update(route);
                await _unitOfWork.SaveChangesAsync(ct);

                await AudioRouteCacheHelper.PublishRoutesUpdateAsync(route.TranslationRoomId, _translationRoomAudioRouteRepository, _translationRoomRepository, _redisConnection, ct);
            }

            return Result.Success(TranslationRoomAudioRouteMapper.ToDto(route));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while updating runtime context for route {RouteId}", routeId);
            return Result.Failure<TranslationRoomAudioRouteDto>(AudioRouteConstants.ErrorUnexpected, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<TranslationRoomAudioRouteDto>> ToggleVoiceCloneAsync(Guid roomId, Guid routeId, ToggleVoiceCloneDto dto, CancellationToken ct = default)
    {
        try
        {
            var route = await _translationRoomAudioRouteRepository.GetByIdAsync(routeId, ct);
            if (route == null)
            {
                return Result.Failure<TranslationRoomAudioRouteDto>(AudioRouteConstants.ErrorRouteNotFound, ErrorCodes.NotFound);
            }

            if (route.TranslationRoomId != roomId)
            {
                return Result.Failure<TranslationRoomAudioRouteDto>(AudioRouteConstants.ErrorRouteNotBelongToRoom, ErrorCodes.ValidationError);
            }

            if (route.Status == AudioRouteStatus.COMPLETED.ToString())
            {
                return Result.Failure<TranslationRoomAudioRouteDto>(AudioRouteConstants.ErrorCannotUpdateCompletedRoute, ErrorCodes.InvalidState);
            }

            if (route.VoiceCloneEnabled != dto.VoiceCloneEnabled)
            {
                route.VoiceCloneEnabled = dto.VoiceCloneEnabled;
                route.UpdatedAt = DateTime.UtcNow;

                _translationRoomAudioRouteRepository.Update(route);
                await _unitOfWork.SaveChangesAsync(ct);

                await AudioRouteCacheHelper.PublishRoutesUpdateAsync(route.TranslationRoomId, _translationRoomAudioRouteRepository, _translationRoomRepository, _redisConnection, ct);
            }

            return Result.Success(TranslationRoomAudioRouteMapper.ToDto(route));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while toggling voice clone for route {RouteId}", routeId);
            return Result.Failure<TranslationRoomAudioRouteDto>(AudioRouteConstants.ErrorUnexpected, ErrorCodes.InternalServerError);
        }
    }
}
