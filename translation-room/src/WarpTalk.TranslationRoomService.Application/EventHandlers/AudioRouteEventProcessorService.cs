using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System;
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
            var routesToUpdate = new List<TranslationRoomAudioRoute>();

            if (routeId.HasValue)
            {
                // Single route event
                var route = await _routeRepository.GetByIdAsync(routeId.Value, ct);
                if (route == null) return Result.Failure(AudioRouteConstants.ErrorRouteNotFound, ErrorCodes.NotFound);

                if (ProcessTransition(route, eventType))
                {
                    routesToUpdate.Add(route);
                }
            }
            else
            {
                // Room-wide event (e.g. host_ended_session)
                var routes = await _routeRepository.GetRoutesByRoomIdAsync(roomId, ct);
                foreach (var route in routes)
                {
                    if (ProcessTransition(route, eventType))
                    {
                        routesToUpdate.Add(route);
                    }
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
            }

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing event {EventType} for Room {RoomId} / Route {RouteId}", eventType, roomId, routeId);
            return Result.Failure(AudioRouteConstants.ErrorInternalProcessingEvent, ErrorCodes.InternalServerError);
        }
    }

    private bool ProcessTransition(TranslationRoomAudioRoute route, AudioRoutingEventType eventType)
    {
        if (!Enum.TryParse<AudioRouteStatus>(route.Status, true, out var currentState))
        {
            // Default or fallback
            currentState = AudioRouteStatus.IDLE;
        }

        var result = _stateMachine.GetNextState(currentState, eventType);
        if (result.IsSuccess && result.Value != currentState)
        {
            route.Status = result.Value.ToString();
            route.UpdatedAt = DateTime.UtcNow;
            
            if (result.Value == AudioRouteStatus.AUDIO_ROUTING_ACTIVE && currentState == AudioRouteStatus.ROUTING_READY)
            {
                route.StartedAt = DateTime.UtcNow;
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
