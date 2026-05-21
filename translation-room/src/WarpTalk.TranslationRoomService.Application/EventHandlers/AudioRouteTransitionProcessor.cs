using Microsoft.Extensions.Logging;
using System;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using WarpTalk.TranslationRoomService.Domain.StateMachines;

namespace WarpTalk.TranslationRoomService.Application.EventHandlers;

public class AudioRouteTransitionProcessor : IAudioRouteTransitionProcessor
{
    private readonly IAudioRouteStateMachine _stateMachine;
    private readonly ILogger<AudioRouteTransitionProcessor> _logger;

    public AudioRouteTransitionProcessor(
        IAudioRouteStateMachine stateMachine,
        ILogger<AudioRouteTransitionProcessor> logger)
    {
        _stateMachine = stateMachine;
        _logger = logger;
    }

    public bool ProcessTransition(TranslationRoomAudioRoute route, AudioRoutingEventType eventType, string? payloadJson = null)
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
