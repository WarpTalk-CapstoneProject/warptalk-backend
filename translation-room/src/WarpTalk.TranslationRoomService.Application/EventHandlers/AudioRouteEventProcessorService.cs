using Microsoft.Extensions.Logging;
using StackExchange.Redis;
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

public class AudioRouteEventProcessorService : IAudioRouteEventProcessorService
{
    private readonly IAudioRouteStateMachine _stateMachine;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITranslationRoomAudioRouteRepository _routeRepository;
    private readonly ITranslationRoomRepository _roomRepository;
    private readonly IConnectionMultiplexer _redisConnection;
    private readonly IArtifactsFinalizationQueue _finalizationQueue;
    private readonly ILogger<AudioRouteEventProcessorService> _logger;

    public AudioRouteEventProcessorService(
        IAudioRouteStateMachine stateMachine,
        IUnitOfWork unitOfWork,
        IConnectionMultiplexer redisConnection,
        IArtifactsFinalizationQueue finalizationQueue,
        ILogger<AudioRouteEventProcessorService> logger)
    {
        _stateMachine = stateMachine;
        _unitOfWork = unitOfWork;
        _routeRepository = _unitOfWork.TranslationRoomAudioRouteRepository;
        _roomRepository = _unitOfWork.TranslationRoomRepository;
        _redisConnection = redisConnection;
        _finalizationQueue = finalizationQueue;
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
            if (IsTelemetryOrTransportEvent(eventType))
            {
                payloadJson = await UpdateTelemetryFlagsAndResolvePayloadAsync(roomId, eventType, payloadJson);
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

                if (ProcessTransition(route, eventType, payloadJson))
                {
                    routesToUpdate.Add(route);
                }
            }

            if (routesToUpdate.Any())
            {
                await _routeRepository.UpdateRoutesAsync(routesToUpdate, ct);
                await _unitOfWork.SaveChangesAsync(ct);

                await AudioRouteCacheHelper.PublishRoutesUpdateAsync(roomId, _routeRepository, _roomRepository, _redisConnection, ct);

                if (routesToUpdate.Any(r => r.Status == AudioRouteStatus.STOPPING.ToString()))
                {
                    _finalizationQueue.QueueFinalization(roomId);
                }

                if (routesToUpdate.Any(r => r.Status == AudioRouteStatus.COMPLETED.ToString()))
                {
                    try
                    {
                        var db = _redisConnection.GetDatabase();
                        await db.KeyDeleteAsync(RedisKeyHelper.GetTranscriptKey(roomId));
                        await db.KeyDeleteAsync(RedisKeyHelper.GetTelemetryStateKey(roomId));
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

    private bool IsTelemetryOrTransportEvent(AudioRoutingEventType eventType)
    {
        return eventType == AudioRoutingEventType.token_exhausted ||
               eventType == AudioRoutingEventType.token_recovered ||
               eventType == AudioRoutingEventType.voice_clone_unavailable ||
               eventType == AudioRoutingEventType.voice_clone_recovered ||
               eventType == AudioRoutingEventType.audio_unavailable ||
               eventType == AudioRoutingEventType.audio_recovered ||
               eventType == AudioRoutingEventType.tts_unavailable ||
               eventType == AudioRoutingEventType.telemetry_state_updated;
    }

    private async Task<string> UpdateTelemetryFlagsAndResolvePayloadAsync(Guid roomId, AudioRoutingEventType eventType, string originalPayload)
    {
        var db = _redisConnection.GetDatabase();
        var hashKey = RedisKeyHelper.GetTelemetryStateKey(roomId);

        // 1. Map event to corresponding volatile flag updates in Redis
        var updates = new List<HashEntry>();
        if (eventType == AudioRoutingEventType.token_exhausted || eventType == AudioRoutingEventType.voice_clone_unavailable)
        {
            updates.Add(new HashEntry("voice_clone_status", "FALLBACK"));
        }
        else if (eventType == AudioRoutingEventType.token_recovered || eventType == AudioRoutingEventType.voice_clone_recovered)
        {
            updates.Add(new HashEntry("voice_clone_status", "NORMAL"));
        }
        else if (eventType == AudioRoutingEventType.audio_unavailable || eventType == AudioRoutingEventType.tts_unavailable)
        {
            updates.Add(new HashEntry("delivery_mode", "TEXT_ONLY"));
        }
        else if (eventType == AudioRoutingEventType.audio_recovered)
        {
            updates.Add(new HashEntry("delivery_mode", "NORMAL"));
        }

        if (updates.Any())
        {
            await db.HashSetAsync(hashKey, updates.ToArray());
            await db.KeyExpireAsync(hashKey, TimeSpan.FromHours(24));
        }

        // 2. Fetch all telemetry flags from Redis to resolve the current unified state
        var stateEntries = await db.HashGetAllAsync(hashKey);
        bool isSttDegraded = false, isTranslationDegraded = false, isTtsDegraded = false;
        string voiceCloneStatus = "NORMAL", deliveryMode = "NORMAL";

        foreach (var entry in stateEntries)
        {
            if (entry.Name == "is_stt_degraded") isSttDegraded = (bool)entry.Value;
            else if (entry.Name == "is_translation_degraded") isTranslationDegraded = (bool)entry.Value;
            else if (entry.Name == "is_tts_degraded") isTtsDegraded = (bool)entry.Value;
            else if (entry.Name == "voice_clone_status") voiceCloneStatus = entry.Value.ToString();
            else if (entry.Name == "delivery_mode") deliveryMode = entry.Value.ToString();
        }

        var resolvedStatus = AudioRoutePriorityResolver.ResolveEffectiveStatus(
            isSttDegraded,
            isTranslationDegraded,
            isTtsDegraded,
            voiceCloneStatus,
            deliveryMode);

        return $"{{\"status\":\"{resolvedStatus}\"}}";
    }

    private bool ProcessTransition(TranslationRoomAudioRoute route, AudioRoutingEventType eventType, string? payloadJson = null)
    {
        if (!Enum.TryParse<AudioRouteStatus>(route.Status, true, out var currentState))
        {
            currentState = AudioRouteStatus.IDLE;
        }

        var result = _stateMachine.GetNextState(currentState, eventType, payloadJson);
        if (result.IsSuccess && result.Value != currentState)
        {
            var nextState = result.Value;
            route.Status = nextState.ToString();
            route.UpdatedAt = DateTime.UtcNow;
            
            if (nextState == AudioRouteStatus.AUDIO_ROUTING_ACTIVE && currentState == AudioRouteStatus.ROUTING_READY)
            {
                route.StartedAt = DateTime.UtcNow;
            }

            // Rule 4: Distinguished logging for Technical vs Billing forced voice clone fallback
            if (nextState == AudioRouteStatus.VOICE_CLONE_FALLBACK)
            {
                if (eventType == AudioRoutingEventType.token_exhausted)
                {
                    _logger.LogWarning("[Voice Clone Fallback] Forced fallback: Participant {ParticipantId} has exhausted their active token balance. Restricting synthesis to standard TTS on Route {RouteId}.", route.SourceParticipantId, route.Id);
                }
                else if (eventType == AudioRoutingEventType.voice_clone_unavailable)
                {
                    _logger.LogWarning("[Voice Clone Fallback] Technical failure: Model synthesizer server offline or overloaded. Falling back to standard TTS on Route {RouteId}.", route.Id);
                }
                else
                {
                    _logger.LogWarning("[Voice Clone Fallback] Fallback triggered by event {EventType} on Route {RouteId}.", eventType, route.Id);
                }
            }
            else
            {
                _logger.LogInformation("Route {RouteId} transitioned from {CurrentState} to {NextState} via event {EventType}.", route.Id, currentState, nextState, eventType);
            }

            return true;
        }
        else if (!result.IsSuccess)
        {
            _logger.LogInformation("State transition rejected for Route {RouteId}: {Reason}", route.Id, result.Error);
        }
        return false;
    }
}
