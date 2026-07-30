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
                            if (TryParseStatus(statusStr, out var targetStatus))
                            {
                                if (IsStreamingState(targetStatus))
                                {
                                    return Result.Success(targetStatus);
                                }
                            }
                        }
                    }
                    catch (System.Text.Json.JsonException)
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
            if (currentState != AudioRouteStatus.SAVING_OUTPUTS && currentState != AudioRouteStatus.COMPLETED)
            {
                return Result.Success(AudioRouteStatus.ENDING);
            }
        }

        // 4. State Machine Transition Table
        var transitionResult = currentState switch
        {
            AudioRouteStatus.PENDING => eventType switch
            {
                AudioRoutingEventType.config_ready => Result.Success(AudioRouteStatus.READY),
                _ => InvalidTransition(currentState, eventType)
            },

            AudioRouteStatus.READY => eventType switch
            {
                AudioRoutingEventType.session_starts => Result.Success(AudioRouteStatus.BROADCASTING),
                _ => InvalidTransition(currentState, eventType)
            },

            AudioRouteStatus.BROADCASTING => eventType switch
            {
                AudioRoutingEventType.room_pause => Result.Success(AudioRouteStatus.PAUSED),
                
                // Degraded / Latency transitions
                AudioRoutingEventType.stt_latency_high => Result.Success(AudioRouteStatus.SPEECH_DELAYED),
                AudioRoutingEventType.translation_latency_high => Result.Success(AudioRouteStatus.TRANSLATION_DELAYED),
                AudioRoutingEventType.tts_latency_high => Result.Success(AudioRouteStatus.VOICE_DELAYED),
                
                // Voice clone fallback path (technical or token exhaustion)
                AudioRoutingEventType.voice_clone_unavailable => Result.Success(AudioRouteStatus.STANDARD_VOICE),
                AudioRoutingEventType.token_exhausted => Result.Success(AudioRouteStatus.STANDARD_VOICE),
                
                // Complete audio failure
                AudioRoutingEventType.audio_unavailable => Result.Success(AudioRouteStatus.CAPTION_ONLY),
                
                _ => InvalidTransition(currentState, eventType)
            },

            AudioRouteStatus.PAUSED => eventType switch
            {
                AudioRoutingEventType.room_resume => Result.Success(AudioRouteStatus.BROADCASTING),
                _ => InvalidTransition(currentState, eventType)
            },

            AudioRouteStatus.SPEECH_DELAYED => eventType switch
            {
                AudioRoutingEventType.stt_recovered => Result.Success(AudioRouteStatus.BROADCASTING),
                AudioRoutingEventType.room_pause => Result.Success(AudioRouteStatus.PAUSED),
                AudioRoutingEventType.audio_unavailable => Result.Success(AudioRouteStatus.CAPTION_ONLY),
                AudioRoutingEventType.voice_clone_unavailable => Result.Success(AudioRouteStatus.STANDARD_VOICE),
                AudioRoutingEventType.token_exhausted => Result.Success(AudioRouteStatus.STANDARD_VOICE),
                _ => InvalidTransition(currentState, eventType)
            },

            AudioRouteStatus.TRANSLATION_DELAYED => eventType switch
            {
                AudioRoutingEventType.translation_recovered => Result.Success(AudioRouteStatus.BROADCASTING),
                AudioRoutingEventType.room_pause => Result.Success(AudioRouteStatus.PAUSED),
                AudioRoutingEventType.audio_unavailable => Result.Success(AudioRouteStatus.CAPTION_ONLY),
                AudioRoutingEventType.voice_clone_unavailable => Result.Success(AudioRouteStatus.STANDARD_VOICE),
                AudioRoutingEventType.token_exhausted => Result.Success(AudioRouteStatus.STANDARD_VOICE),
                _ => InvalidTransition(currentState, eventType)
            },

            AudioRouteStatus.VOICE_DELAYED => eventType switch
            {
                AudioRoutingEventType.tts_recovered => Result.Success(AudioRouteStatus.BROADCASTING),
                AudioRoutingEventType.room_pause => Result.Success(AudioRouteStatus.PAUSED),
                AudioRoutingEventType.tts_unavailable => Result.Success(AudioRouteStatus.CAPTION_ONLY),
                AudioRoutingEventType.audio_unavailable => Result.Success(AudioRouteStatus.CAPTION_ONLY),
                AudioRoutingEventType.voice_clone_unavailable => Result.Success(AudioRouteStatus.STANDARD_VOICE),
                AudioRoutingEventType.token_exhausted => Result.Success(AudioRouteStatus.STANDARD_VOICE),
                _ => InvalidTransition(currentState, eventType)
            },

            AudioRouteStatus.STANDARD_VOICE => eventType switch
            {
                // Both technical recovery and token recharge return the route to broadcasting.
                AudioRoutingEventType.voice_clone_recovered => Result.Success(AudioRouteStatus.BROADCASTING),
                AudioRoutingEventType.token_recovered => Result.Success(AudioRouteStatus.BROADCASTING),
                AudioRoutingEventType.room_pause => Result.Success(AudioRouteStatus.PAUSED),
                AudioRoutingEventType.tts_unavailable => Result.Success(AudioRouteStatus.CAPTION_ONLY),
                AudioRoutingEventType.audio_unavailable => Result.Success(AudioRouteStatus.CAPTION_ONLY),
                _ => InvalidTransition(currentState, eventType)
            },

            AudioRouteStatus.CAPTION_ONLY => eventType switch
            {
                AudioRoutingEventType.audio_recovered => Result.Success(AudioRouteStatus.BROADCASTING),
                AudioRoutingEventType.room_pause => Result.Success(AudioRouteStatus.PAUSED),
                _ => InvalidTransition(currentState, eventType)
            },

            AudioRouteStatus.ENDING => eventType switch
            {
                AudioRoutingEventType.flush_runtime => Result.Success(AudioRouteStatus.SAVING_OUTPUTS),
                _ => InvalidTransition(currentState, eventType)
            },

            AudioRouteStatus.SAVING_OUTPUTS => eventType switch
            {
                AudioRoutingEventType.outputs_linked => Result.Success(AudioRouteStatus.COMPLETED),
                AudioRoutingEventType.finalization_failed => Result.Success(AudioRouteStatus.SAVE_FAILED),
                AudioRoutingEventType.finalization_abandoned => Result.Success(AudioRouteStatus.COMPLETED),
                _ => InvalidTransition(currentState, eventType)
            },

            AudioRouteStatus.SAVE_FAILED => eventType switch
            {
                AudioRoutingEventType.flush_runtime => Result.Success(AudioRouteStatus.SAVING_OUTPUTS),
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
        return status == AudioRouteStatus.BROADCASTING ||
               status == AudioRouteStatus.SPEECH_DELAYED ||
               status == AudioRouteStatus.TRANSLATION_DELAYED ||
               status == AudioRouteStatus.VOICE_DELAYED ||
               status == AudioRouteStatus.STANDARD_VOICE ||
               status == AudioRouteStatus.CAPTION_ONLY;
    }

    private static bool TryParseStatus(string? status, out AudioRouteStatus parsed)
    {
        if (System.Enum.TryParse<AudioRouteStatus>(status, true, out parsed))
        {
            return true;
        }

        parsed = status?.ToUpperInvariant() switch
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

        return status is not null;
    }

    private Result<AudioRouteStatus> InvalidTransition(AudioRouteStatus current, AudioRoutingEventType eventType)
    {
        return Result.Failure<AudioRouteStatus>(
            $"Invalid transition from {current} via event {eventType}", 
            ErrorCodes.InvalidState);
    }
}
