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
        if (eventType == AudioRoutingEventType.host_ends_session)
        {
            // Even if degraded or active, force shutdown
            if (currentState != AudioRouteStatus.FINALIZING_ARTIFACTS && currentState != AudioRouteStatus.COMPLETED)
                return Result.Success(AudioRouteStatus.STOPPING);
        }

        if (eventType == AudioRoutingEventType.transcript_recording_summary_linked)
        {
            return Result.Success(AudioRouteStatus.COMPLETED);
        }

        return currentState switch
        {
            AudioRouteStatus.IDLE => eventType switch
            {
                AudioRoutingEventType.participants_and_languages_configured => Result.Success(AudioRouteStatus.ROUTING_READY),
                _ => InvalidTransition(currentState, eventType)
            },

            AudioRouteStatus.ROUTING_READY => eventType switch
            {
                AudioRoutingEventType.session_starts => Result.Success(AudioRouteStatus.AUDIO_ROUTING_ACTIVE),
                _ => InvalidTransition(currentState, eventType)
            },

            AudioRouteStatus.AUDIO_ROUTING_ACTIVE => eventType switch
            {
                AudioRoutingEventType.host_pauses_session => Result.Success(AudioRouteStatus.PAUSED),
                _ => InvalidTransition(currentState, eventType)
            },


            AudioRouteStatus.PAUSED => eventType switch
            {
                AudioRoutingEventType.host_resumes_session => Result.Success(AudioRouteStatus.AUDIO_ROUTING_ACTIVE),
                _ => InvalidTransition(currentState, eventType)
            },

            AudioRouteStatus.STOPPING => eventType switch
            {
                // Internal auto-transition trigger
                AudioRoutingEventType.stop_routing_and_flush_data => Result.Success(AudioRouteStatus.FINALIZING_ARTIFACTS),
                _ => InvalidTransition(currentState, eventType)
            },

            AudioRouteStatus.FINALIZING_ARTIFACTS => eventType switch
            {
                // The worker explicitly sends this when artifacts are uploaded
                AudioRoutingEventType.transcript_recording_summary_linked => Result.Success(AudioRouteStatus.COMPLETED),
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
