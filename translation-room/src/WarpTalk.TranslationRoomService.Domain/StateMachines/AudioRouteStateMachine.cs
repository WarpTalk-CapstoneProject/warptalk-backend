using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Domain.Enums;

namespace WarpTalk.TranslationRoomService.Domain.StateMachines;

public class AudioRouteStateMachine : IAudioRouteStateMachine
{
    public Result<AudioRouteStatus> GetNextState(AudioRouteStatus currentState, AudioRoutingEventType eventType, string? payloadJson = null)
    {
        // 1. Terminal States - COMPLETED is a sink state
        if (currentState == AudioRouteStatus.COMPLETED)
        {
            return Result.Success(AudioRouteStatus.COMPLETED);
        }

        // 2. Telemetry Priority Resolver Override
        if (eventType == AudioRoutingEventType.telemetry_state_updated)
        {
            if (IsStreamingState(currentState))
            {
                if (!string.IsNullOrEmpty(payloadJson))
                {
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(payloadJson);
                        if (doc.RootElement.TryGetProperty("status", out var prop))
                        {
                            var statusStr = prop.GetString();
                            if (System.Enum.TryParse<AudioRouteStatus>(statusStr, true, out var targetStatus))
                            {
                                if (IsStreamingState(targetStatus))
                                {
                                    return Result.Success(targetStatus);
                                }
                            }
                        }
                    }
                    catch
                    {
                        // Fall through to failure
                    }
                }
                return Result.Failure<AudioRouteStatus>("Invalid target status for telemetry update", ErrorCodes.InvalidState);
            }
        }

        // 3. Priority Override Triggers (Session Ends or System Disables)
        if (eventType == AudioRoutingEventType.session_ends || eventType == AudioRoutingEventType.system_disabled)
        {
            if (currentState != AudioRouteStatus.FINALIZING_ARTIFACTS && currentState != AudioRouteStatus.COMPLETED)
            {
                return Result.Success(AudioRouteStatus.STOPPING);
            }
        }

        // 4. State Machine Transition Table
        var transitionResult = currentState switch
        {
            AudioRouteStatus.IDLE => eventType switch
            {
                AudioRoutingEventType.config_ready => Result.Success(AudioRouteStatus.ROUTING_READY),
                _ => InvalidTransition(currentState, eventType)
            },

            AudioRouteStatus.ROUTING_READY => eventType switch
            {
                AudioRoutingEventType.session_starts => Result.Success(AudioRouteStatus.AUDIO_ROUTING_ACTIVE),
                _ => InvalidTransition(currentState, eventType)
            },

            AudioRouteStatus.AUDIO_ROUTING_ACTIVE => eventType switch
            {
                AudioRoutingEventType.room_pause => Result.Success(AudioRouteStatus.AUDIO_ROUTING_PAUSED),
                
                // Degraded / Latency transitions
                AudioRoutingEventType.stt_latency_high => Result.Success(AudioRouteStatus.STT_DEGRADED),
                AudioRoutingEventType.translation_latency_high => Result.Success(AudioRouteStatus.TRANSLATION_DEGRADED),
                AudioRoutingEventType.tts_latency_high => Result.Success(AudioRouteStatus.TTS_DEGRADED),
                
                // Voice clone fallback path (technical or token exhaustion)
                AudioRoutingEventType.voice_clone_unavailable => Result.Success(AudioRouteStatus.VOICE_CLONE_FALLBACK),
                AudioRoutingEventType.token_exhausted => Result.Success(AudioRouteStatus.VOICE_CLONE_FALLBACK),
                
                // Complete audio failure
                AudioRoutingEventType.audio_unavailable => Result.Success(AudioRouteStatus.TEXT_ONLY_MODE),
                
                _ => InvalidTransition(currentState, eventType)
            },

            AudioRouteStatus.AUDIO_ROUTING_PAUSED => eventType switch
            {
                AudioRoutingEventType.room_resume => Result.Success(AudioRouteStatus.AUDIO_ROUTING_ACTIVE),
                _ => InvalidTransition(currentState, eventType)
            },

            AudioRouteStatus.STT_DEGRADED => eventType switch
            {
                AudioRoutingEventType.stt_recovered => Result.Success(AudioRouteStatus.AUDIO_ROUTING_ACTIVE),
                AudioRoutingEventType.room_pause => Result.Success(AudioRouteStatus.AUDIO_ROUTING_PAUSED),
                AudioRoutingEventType.audio_unavailable => Result.Success(AudioRouteStatus.TEXT_ONLY_MODE),
                AudioRoutingEventType.voice_clone_unavailable => Result.Success(AudioRouteStatus.VOICE_CLONE_FALLBACK),
                AudioRoutingEventType.token_exhausted => Result.Success(AudioRouteStatus.VOICE_CLONE_FALLBACK),
                _ => InvalidTransition(currentState, eventType)
            },

            AudioRouteStatus.TRANSLATION_DEGRADED => eventType switch
            {
                AudioRoutingEventType.translation_recovered => Result.Success(AudioRouteStatus.AUDIO_ROUTING_ACTIVE),
                AudioRoutingEventType.room_pause => Result.Success(AudioRouteStatus.AUDIO_ROUTING_PAUSED),
                AudioRoutingEventType.audio_unavailable => Result.Success(AudioRouteStatus.TEXT_ONLY_MODE),
                AudioRoutingEventType.voice_clone_unavailable => Result.Success(AudioRouteStatus.VOICE_CLONE_FALLBACK),
                AudioRoutingEventType.token_exhausted => Result.Success(AudioRouteStatus.VOICE_CLONE_FALLBACK),
                _ => InvalidTransition(currentState, eventType)
            },

            AudioRouteStatus.TTS_DEGRADED => eventType switch
            {
                AudioRoutingEventType.tts_recovered => Result.Success(AudioRouteStatus.AUDIO_ROUTING_ACTIVE),
                AudioRoutingEventType.room_pause => Result.Success(AudioRouteStatus.AUDIO_ROUTING_PAUSED),
                AudioRoutingEventType.tts_unavailable => Result.Success(AudioRouteStatus.TEXT_ONLY_MODE),
                AudioRoutingEventType.audio_unavailable => Result.Success(AudioRouteStatus.TEXT_ONLY_MODE),
                AudioRoutingEventType.voice_clone_unavailable => Result.Success(AudioRouteStatus.VOICE_CLONE_FALLBACK),
                AudioRoutingEventType.token_exhausted => Result.Success(AudioRouteStatus.VOICE_CLONE_FALLBACK),
                _ => InvalidTransition(currentState, eventType)
            },

            AudioRouteStatus.VOICE_CLONE_FALLBACK => eventType switch
            {
                // Both technical recovery or token recharge returns route to ACTIVE
                AudioRoutingEventType.voice_clone_recovered => Result.Success(AudioRouteStatus.AUDIO_ROUTING_ACTIVE),
                AudioRoutingEventType.token_recovered => Result.Success(AudioRouteStatus.AUDIO_ROUTING_ACTIVE),
                AudioRoutingEventType.room_pause => Result.Success(AudioRouteStatus.AUDIO_ROUTING_PAUSED),
                AudioRoutingEventType.tts_unavailable => Result.Success(AudioRouteStatus.TEXT_ONLY_MODE),
                AudioRoutingEventType.audio_unavailable => Result.Success(AudioRouteStatus.TEXT_ONLY_MODE),
                _ => InvalidTransition(currentState, eventType)
            },

            AudioRouteStatus.TEXT_ONLY_MODE => eventType switch
            {
                AudioRoutingEventType.audio_recovered => Result.Success(AudioRouteStatus.AUDIO_ROUTING_ACTIVE),
                AudioRoutingEventType.room_pause => Result.Success(AudioRouteStatus.AUDIO_ROUTING_PAUSED),
                _ => InvalidTransition(currentState, eventType)
            },

            AudioRouteStatus.STOPPING => eventType switch
            {
                AudioRoutingEventType.flush_runtime => Result.Success(AudioRouteStatus.FINALIZING_ARTIFACTS),
                _ => InvalidTransition(currentState, eventType)
            },

            AudioRouteStatus.FINALIZING_ARTIFACTS => eventType switch
            {
                AudioRoutingEventType.outputs_linked => Result.Success(AudioRouteStatus.COMPLETED),
                AudioRoutingEventType.finalization_failed => Result.Success(AudioRouteStatus.FINALIZING_ARTIFACTS_FAILED),
                AudioRoutingEventType.finalization_abandoned => Result.Success(AudioRouteStatus.COMPLETED),
                _ => InvalidTransition(currentState, eventType)
            },

            AudioRouteStatus.FINALIZING_ARTIFACTS_FAILED => eventType switch
            {
                AudioRoutingEventType.flush_runtime => Result.Success(AudioRouteStatus.FINALIZING_ARTIFACTS),
                AudioRoutingEventType.finalization_abandoned => Result.Success(AudioRouteStatus.COMPLETED),
                _ => InvalidTransition(currentState, eventType)
            },

            _ => Result.Failure<AudioRouteStatus>($"Unhandled state {currentState}", ErrorCodes.InvalidState)
        };

        // 5. Silent Acceptance Rule for telemetry events
        if (!transitionResult.IsSuccess && IsHighFrequencyTelemetryEvent(eventType))
        {
            // Silently return success with the current state to prevent error pollution in logs
            return Result.Success(currentState);
        }

        return transitionResult;
    }

    private bool IsHighFrequencyTelemetryEvent(AudioRoutingEventType eventType)
    {
        return eventType == AudioRoutingEventType.stt_latency_high ||
               eventType == AudioRoutingEventType.stt_recovered ||
               eventType == AudioRoutingEventType.translation_latency_high ||
               eventType == AudioRoutingEventType.translation_recovered ||
               eventType == AudioRoutingEventType.tts_latency_high ||
               eventType == AudioRoutingEventType.tts_recovered;
    }

    private bool IsStreamingState(AudioRouteStatus status)
    {
        return status == AudioRouteStatus.AUDIO_ROUTING_ACTIVE ||
               status == AudioRouteStatus.STT_DEGRADED ||
               status == AudioRouteStatus.TRANSLATION_DEGRADED ||
               status == AudioRouteStatus.TTS_DEGRADED ||
               status == AudioRouteStatus.VOICE_CLONE_FALLBACK ||
               status == AudioRouteStatus.TEXT_ONLY_MODE;
    }

    private Result<AudioRouteStatus> InvalidTransition(AudioRouteStatus current, AudioRoutingEventType eventType)
    {
        return Result.Failure<AudioRouteStatus>(
            $"Invalid transition from {current} via event {eventType}", 
            ErrorCodes.InvalidState);
    }
}
