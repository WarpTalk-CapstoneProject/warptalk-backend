using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Domain.Enums;

namespace WarpTalk.TranslationRoomService.Domain.StateMachines;

public class AudioRouteStateMachine : IAudioRouteStateMachine
{
    public Result<AudioRouteStatus> GetNextState(AudioRouteStatus currentState, AudioRoutingEventType eventType)
    {
        // Terminal states cannot transition out unless explicitly restarted (which is handled via IDLE mapping)
        if (currentState == AudioRouteStatus.COMPLETED)
        {
            return Result.Failure<AudioRouteStatus>("Route is COMPLETED and cannot transition further.", ErrorCodes.InvalidState);
        }

        // Priority Override: If host ends session, immediately transition to STOPPING
        if (eventType == AudioRoutingEventType.host_ended_session)
        {
            // Even if degraded or active, force shutdown
            if (currentState != AudioRouteStatus.FINALIZING_ARTIFACTS && currentState != AudioRouteStatus.COMPLETED)
                return Result.Success(AudioRouteStatus.STOPPING);
        }

        if (eventType == AudioRoutingEventType.artifacts_finalized)
        {
            return Result.Success(AudioRouteStatus.COMPLETED);
        }

        return currentState switch
        {
            AudioRouteStatus.IDLE => eventType switch
            {
                AudioRoutingEventType.participants_configured => Result.Success(AudioRouteStatus.ROUTING_READY),
                _ => InvalidTransition(currentState, eventType)
            },

            AudioRouteStatus.ROUTING_READY => eventType switch
            {
                AudioRoutingEventType.session_started => Result.Success(AudioRouteStatus.AUDIO_ROUTING_ACTIVE),
                _ => InvalidTransition(currentState, eventType)
            },

            AudioRouteStatus.AUDIO_ROUTING_ACTIVE => eventType switch
            {
                AudioRoutingEventType.translation_latency_high => Result.Success(AudioRouteStatus.TRANSLATION_DEGRADED),
                AudioRoutingEventType.voice_quality_degraded => Result.Success(AudioRouteStatus.VOICE_QUALITY_DEGRADED),
                AudioRoutingEventType.audio_output_unavailable => Result.Success(AudioRouteStatus.TEXT_ONLY_MODE),
                _ => InvalidTransition(currentState, eventType)
            },

            AudioRouteStatus.TRANSLATION_DEGRADED => eventType switch
            {
                AudioRoutingEventType.translation_recovered => Result.Success(AudioRouteStatus.AUDIO_ROUTING_ACTIVE),
                _ => InvalidTransition(currentState, eventType)
            },

            AudioRouteStatus.VOICE_QUALITY_DEGRADED => eventType switch
            {
                AudioRoutingEventType.voice_recovered => Result.Success(AudioRouteStatus.AUDIO_ROUTING_ACTIVE),
                _ => InvalidTransition(currentState, eventType)
            },

            AudioRouteStatus.TEXT_ONLY_MODE => eventType switch
            {
                AudioRoutingEventType.audio_recovered => Result.Success(AudioRouteStatus.AUDIO_ROUTING_ACTIVE),
                _ => InvalidTransition(currentState, eventType)
            },

            AudioRouteStatus.STOPPING => eventType switch
            {
                // Internal auto-transition trigger
                AudioRoutingEventType.routing_stopped_and_flushed => Result.Success(AudioRouteStatus.FINALIZING_ARTIFACTS),
                _ => InvalidTransition(currentState, eventType)
            },

            AudioRouteStatus.FINALIZING_ARTIFACTS => eventType switch
            {
                // The worker explicitly sends this when artifacts are uploaded
                AudioRoutingEventType.artifacts_finalized => Result.Success(AudioRouteStatus.COMPLETED),
                _ => InvalidTransition(currentState, eventType)
            },

            _ => Result.Failure<AudioRouteStatus>($"Unhandled state {currentState}", ErrorCodes.InvalidState)
        };
    }

    private Result<AudioRouteStatus> InvalidTransition(AudioRouteStatus current, AudioRoutingEventType eventType)
    {
        return Result.Failure<AudioRouteStatus>(
            $"Invalid transition from {current} via event {eventType}", 
            ErrorCodes.InvalidState);
    }
}
