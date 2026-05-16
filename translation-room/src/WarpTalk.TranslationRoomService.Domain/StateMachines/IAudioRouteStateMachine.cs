using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Domain.Enums;

namespace WarpTalk.TranslationRoomService.Domain.StateMachines;

public interface IAudioRouteStateMachine
{
    /// <summary>
    /// Computes the next state based on the current state and the incoming event.
    /// Applies guard clauses and priority rules.
    /// </summary>
    /// <param name="currentState">The current status of the audio route</param>
    /// <param name="eventType">The event that occurred</param>
    /// <returns>A Result containing the next AudioRouteStatus if transition is allowed, or a Failure if transition is invalid.</returns>
    Result<AudioRouteStatus> GetNextState(AudioRouteStatus currentState, AudioRoutingEventType eventType);
}
