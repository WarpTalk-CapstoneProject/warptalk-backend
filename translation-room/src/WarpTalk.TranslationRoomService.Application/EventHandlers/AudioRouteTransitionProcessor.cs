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
        var currentState = ParseStatus(route.Status);

        var result = _stateMachine.GetNextState(currentState, eventType, payloadJson);
        if (result.IsSuccess && result.Value != currentState)
        {
            var nextState = result.Value;
            route.Status = nextState.ToString();
            route.UpdatedAt = DateTime.UtcNow;
            
            if (nextState == AudioRouteStatus.BROADCASTING && currentState == AudioRouteStatus.READY)
            {
                route.StartedAt = DateTime.UtcNow;
            }

            // Rule 4: Distinguished logging for Technical vs Billing forced voice clone fallback
            if (nextState == AudioRouteStatus.STANDARD_VOICE)
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

    private static AudioRouteStatus ParseStatus(string? status)
    {
        if (Enum.TryParse<AudioRouteStatus>(status, true, out var parsed))
        {
            return parsed;
        }

        return status?.ToUpperInvariant() switch
        {
            "IDLE" => AudioRouteStatus.PENDING,
            "ROUTING_READY" => AudioRouteStatus.READY,
            "AUDIO_ROUTING_ACTIVE" => AudioRouteStatus.BROADCASTING,
            "AUDIO_ROUTING_PAUSED" => AudioRouteStatus.PAUSED,
            "STT_DEGRADED" => AudioRouteStatus.SPEECH_DELAYED,
            "TRANSLATION_DEGRADED" => AudioRouteStatus.TRANSLATION_DELAYED,
            "TTS_DEGRADED" => AudioRouteStatus.VOICE_DELAYED,
            "VOICE_CLONE_FALLBACK" => AudioRouteStatus.STANDARD_VOICE,
            "TEXT_ONLY_MODE" => AudioRouteStatus.CAPTION_ONLY,
            "STOPPING" => AudioRouteStatus.ENDING,
            "FINALIZING_ARTIFACTS" => AudioRouteStatus.SAVING_OUTPUTS,
            "FINALIZING_ARTIFACTS_FAILED" => AudioRouteStatus.SAVE_FAILED,
            _ => AudioRouteStatus.PENDING
        };
    }
}
