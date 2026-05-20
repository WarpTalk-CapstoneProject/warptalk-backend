using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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

namespace WarpTalk.TranslationRoomService.Application.Services;

public class TranslationRoomAudioRouteService : ITranslationRoomAudioRouteService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITranslationRoomRepository _translationRoomRepository;
    private readonly ITranslationRoomParticipantRepository _translationRoomParticipantRepository;
    private readonly ITranslationRoomAudioRouteRepository _translationRoomAudioRouteRepository;
    private readonly IAudioRouteCacheService _audioRouteCacheService;
    private readonly IAudioRouteEventProcessor _eventProcessor;
    private readonly ILanguagePolicy _languagePolicy;
    private readonly ILogger<TranslationRoomAudioRouteService> _logger;

    public TranslationRoomAudioRouteService(
        IUnitOfWork unitOfWork, 
        IAudioRouteCacheService audioRouteCacheService,
        IAudioRouteEventProcessor eventProcessor,
        ILanguagePolicy languagePolicy,
        ILogger<TranslationRoomAudioRouteService> logger)
    {
        _unitOfWork = unitOfWork;
        _translationRoomRepository = _unitOfWork.TranslationRoomRepository;
        _translationRoomParticipantRepository = _unitOfWork.TranslationRoomParticipantRepository;
        _translationRoomAudioRouteRepository = _unitOfWork.TranslationRoomAudioRouteRepository;
        _audioRouteCacheService = audioRouteCacheService;
        _eventProcessor = eventProcessor;
        _languagePolicy = languagePolicy;
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
            if (participants == null || !participants.Any())
            {
                return Result.Failure<List<TranslationRoomAudioRouteDto>>(AudioRouteConstants.ErrorNoParticipantsInRoom, ErrorCodes.InvalidState);
            }

            var existingRoutes = await _translationRoomAudioRouteRepository.GetRoutesByRoomIdAsync(roomId, ct);
            var updatedRoutes = new List<TranslationRoomAudioRoute>();
            var newRoutes = new List<TranslationRoomAudioRoute>();

            var sourceLanguage = room.SourceLanguage;
            var targetLanguagesList = room.TargetLanguages.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            // Generate full-mesh audio routing pathways
            foreach (var speaker in participants)
            {
                foreach (var listener in participants)
                {
                    if (speaker.Id == listener.Id) continue;

                    var sourceLang = speaker.SpeakLanguage ?? sourceLanguage;
                    var targetLang = listener.ListenLanguage ?? targetLanguagesList.FirstOrDefault(l => l != sourceLang) ?? sourceLanguage;

                    if (!_languagePolicy.IsTranslationRequired(sourceLang, targetLang)) continue; // Direct audio routing handles same languages bypasses MT

                    var existingRoute = existingRoutes.FirstOrDefault(r => 
                        r.SourceParticipantId == speaker.Id && 
                        r.TargetParticipantId == listener.Id);

                    if (existingRoute != null)
                    {
                        bool isRouteStale = existingRoute.SourceLanguage != sourceLang || existingRoute.TargetLanguage != targetLang;
                        if (isRouteStale)
                        {
                            existingRoute.SourceLanguage = sourceLang;
                            existingRoute.TargetLanguage = targetLang;
                            existingRoute.UpdatedAt = DateTime.UtcNow;
                            updatedRoutes.Add(existingRoute);
                        }
                    }
                    else
                    {
                        var route = new TranslationRoomAudioRoute
                        {
                            Id = Guid.NewGuid(),
                            TranslationRoomId = roomId,
                            SourceParticipantId = speaker.Id,
                            TargetParticipantId = listener.Id,
                            SourceLanguage = sourceLang,
                            TargetLanguage = targetLang,
                            VoiceCloneEnabled = true,
                            Status = AudioRouteStatus.IDLE.ToString(),
                            CreatedAt = DateTime.UtcNow,
                            UpdatedAt = DateTime.UtcNow
                        };
                        newRoutes.Add(route);
                    }
                }
            }

            // Remove obsolete routes (e.g. participants left the room)
            var activeParticipantIds = participants.Select(p => p.Id).ToHashSet();
            var obsoleteRoutes = existingRoutes
                .Where(r => !activeParticipantIds.Contains(r.SourceParticipantId) || !activeParticipantIds.Contains(r.TargetParticipantId))
                .ToList();

            if (newRoutes.Any())
            {
                await _translationRoomAudioRouteRepository.AddRoutesAsync(newRoutes, ct);
            }

            if (updatedRoutes.Any())
            {
                await _translationRoomAudioRouteRepository.UpdateRoutesAsync(updatedRoutes, ct);
            }

            if (obsoleteRoutes.Any())
            {
                await _translationRoomAudioRouteRepository.RemoveRoutesAsync(obsoleteRoutes, ct);
            }

            if (newRoutes.Any() || updatedRoutes.Any() || obsoleteRoutes.Any())
            {
                await _unitOfWork.SaveChangesAsync(ct);
                await _audioRouteCacheService.PublishRoutesUpdateAsync(roomId, ct);
            }

            var allRoutes = await _translationRoomAudioRouteRepository.GetRoutesByRoomIdAsync(roomId, ct);
            var dtos = allRoutes.Select(TranslationRoomAudioRouteMapper.ToDto).ToList();

            return Result.Success(dtos);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while generating audio routing mesh for Room {RoomId}", roomId);
            return Result.Failure<List<TranslationRoomAudioRouteDto>>(AudioRouteConstants.ErrorUnexpected, ErrorCodes.InternalServerError);
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
            _logger.LogError(ex, "Error occurred while fetching audio routing mesh for Room {RoomId}", roomId);
            return Result.Failure<List<TranslationRoomAudioRouteDto>>(AudioRouteConstants.ErrorUnexpected, ErrorCodes.InternalServerError);
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

                await _audioRouteCacheService.PublishRoutesUpdateAsync(route.TranslationRoomId, ct);
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

                await _audioRouteCacheService.PublishRoutesUpdateAsync(route.TranslationRoomId, ct);
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
