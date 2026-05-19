using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using WarpTalk.TranslationRoomService.Domain.StateMachines;

namespace WarpTalk.TranslationRoomService.Application.EventHandlers;

public class AudioRouteEventProcessor : IAudioRouteEventProcessor
{
    private readonly IAudioRouteTransitionProcessor _transitionProcessor;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITranslationRoomAudioRouteRepository _routeRepository;
    private readonly ITranslationRoomRepository _roomRepository;
    private readonly IRedisStateRepository _redisStateRepo;
    private readonly IArtifactsFinalizationQueue _finalizationQueue;
    private readonly ITelemetryStateService _telemetryStateService;
    private readonly IAudioRouteCacheService _audioRouteCacheService;
    private readonly ILogger<AudioRouteEventProcessor> _logger;

    public AudioRouteEventProcessor(
        IAudioRouteTransitionProcessor transitionProcessor,
        IUnitOfWork unitOfWork,
        IRedisStateRepository redisStateRepo,
        IArtifactsFinalizationQueue finalizationQueue,
        ITelemetryStateService telemetryStateService,
        IAudioRouteCacheService audioRouteCacheService,
        ILogger<AudioRouteEventProcessor> logger)
    {
        _transitionProcessor = transitionProcessor;
        _unitOfWork = unitOfWork;
        _routeRepository = _unitOfWork.TranslationRoomAudioRouteRepository;
        _roomRepository = _unitOfWork.TranslationRoomRepository;
        _redisStateRepo = redisStateRepo;
        _finalizationQueue = finalizationQueue;
        _telemetryStateService = telemetryStateService;
        _audioRouteCacheService = audioRouteCacheService;
        _logger = logger;
    }

    public async Task<Result> ProcessEventAsync(Guid roomId, Guid? routeId, string eventTypeStr, string payloadJson, CancellationToken ct = default)
    {
        if (!Enum.TryParse<AudioRoutingEventType>(eventTypeStr, true, out var eventType))
        {
            _logger.LogWarning("Unknown event type received: {EventType}", eventTypeStr);
            return Result.Failure(AudioRouteConstants.ErrorUnknownEventType, ErrorCodes.ValidationError);
        }

        try
        {
            var originalEventType = eventType;
            if (_telemetryStateService.IsTelemetryOrTransportEvent(eventType))
            {
                payloadJson = await _telemetryStateService.UpdateTransportFlagsAndResolvePayloadAsync(roomId, eventType);
                eventType = AudioRoutingEventType.telemetry_state_updated;
            }

            var routes = await _routeRepository.GetRoutesByRoomIdAsync(roomId, ct);
            var routesToUpdate = new List<TranslationRoomAudioRoute>();

            // Parse targetParticipantId/userId from payload if present
            Guid? targetParticipantId = null;
            if (!string.IsNullOrEmpty(payloadJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(payloadJson);
                    if (doc.RootElement.TryGetProperty("participantId", out var prop) && Guid.TryParse(prop.GetString(), out var pId))
                    {
                        targetParticipantId = pId;
                    }
                    else if (doc.RootElement.TryGetProperty("userId", out var uIdProp) && Guid.TryParse(uIdProp.GetString(), out var uId))
                    {
                        targetParticipantId = uId;
                    }
                }
                catch (JsonException)
                {
                    _logger.LogDebug("Payload JSON could not be parsed or is not structured as expected: {Payload}", payloadJson);
                }
            }

            // Determine which routes to evaluate
            List<TranslationRoomAudioRoute> targetRoutes;

            if (routeId.HasValue)
            {
                var singleRoute = await _routeRepository.GetByIdAsync(routeId.Value, ct);
                targetRoutes = singleRoute != null ? new List<TranslationRoomAudioRoute> { singleRoute } : new List<TranslationRoomAudioRoute>();
            }
            else if (targetParticipantId.HasValue)
            {
                // Normalize billing events (token_exhausted/token_recovered) and session termination (session_ends)
                if (originalEventType == AudioRoutingEventType.token_exhausted || originalEventType == AudioRoutingEventType.token_recovered)
                {
                    // Billing fallback only affects outbound routes where this participant is the source/speaker
                    targetRoutes = routes.Where(r => r.SourceParticipantId == targetParticipantId.Value).ToList();
                }
                else if (originalEventType == AudioRoutingEventType.session_ends)
                {
                    // A participant leaving/kicked/grace-period-expired affects any route where they are source or target
                    targetRoutes = routes.Where(r => r.SourceParticipantId == targetParticipantId.Value || r.TargetParticipantId == targetParticipantId.Value).ToList();
                }
                else
                {
                    targetRoutes = routes.ToList();
                }
            }
            else
            {
                targetRoutes = routes.ToList();
            }

            // Process state transitions with business filters
            foreach (var route in targetRoutes)
            {
                // Rule 1: Context Protection for billing limits
                if (originalEventType == AudioRoutingEventType.token_exhausted || originalEventType == AudioRoutingEventType.token_recovered)
                {
                    if (!route.VoiceCloneEnabled)
                    {
                        _logger.LogInformation("Event {EventType} ignored for Route {RouteId} because Voice Cloning is disabled on this route.", originalEventType, route.Id);
                        continue;
                    }
                }

                if (_transitionProcessor.ProcessTransition(route, eventType, payloadJson))
                {
                    routesToUpdate.Add(route);
                }
            }

            if (routesToUpdate.Any())
            {
                await _routeRepository.UpdateRoutesAsync(routesToUpdate, ct);
                await _unitOfWork.SaveChangesAsync(ct);

                // Publish updates through the standard pipeline (decoupled inside helper, handles external connection natively)
                await _audioRouteCacheService.PublishRoutesUpdateAsync(roomId, ct);

                if (routesToUpdate.Any(r => r.Status == AudioRouteStatus.STOPPING.ToString()))
                {
                    _finalizationQueue.QueueFinalization(roomId);
                }

                if (routesToUpdate.Any(r => r.Status == AudioRouteStatus.COMPLETED.ToString()))
                {
                    try
                    {
                        await _redisStateRepo.KeyDeleteAsync(CacheKeyHelper.GetTranscriptKey(roomId));
                        await _redisStateRepo.KeyDeleteAsync(CacheKeyHelper.GetTelemetryStateKey(roomId));
                        _logger.LogInformation("Proactively cleaned up Redis transcript cache and telemetry state key for completed room {RoomId}", roomId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to perform proactive Redis cleanup for Room {RoomId} on route completion", roomId);
                    }
                }
            }

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing event {EventType} for Room {RoomId} / Route {RouteId}", eventType, roomId, routeId);
            return Result.Failure(AudioRouteConstants.ErrorInternalProcessingEvent, ErrorCodes.InternalServerError);
        }
    }
}
