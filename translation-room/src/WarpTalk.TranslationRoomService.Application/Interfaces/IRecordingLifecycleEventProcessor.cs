using WarpTalk.Shared;
using WarpTalk.Shared.Events;

namespace WarpTalk.TranslationRoomService.Application.Interfaces;

/// <summary>
/// Turns meeting-service's recording lifecycle events into ONE <c>OPTIONAL_RECORDING</c> artifact
/// row per LiveKit egress, keyed by <c>ProviderArtifactId == EgressId</c>.
///
/// Each overload returns <c>true</c> when it wrote something and <c>false</c> when the delivery was
/// a no-op (duplicate, out of order, or a transition the state machine refuses). Both are success:
/// the stream message is acknowledged either way.
/// </summary>
public interface IRecordingLifecycleEventProcessor
{
    Task<Result<bool>> ProcessAsync(
        EventEnvelope<MeetingRecordingStartedEventPayload> envelope,
        CancellationToken ct = default);

    Task<Result<bool>> ProcessAsync(
        EventEnvelope<MeetingRecordingCompletedEventPayload> envelope,
        CancellationToken ct = default);

    Task<Result<bool>> ProcessAsync(
        EventEnvelope<MeetingRecordingFailedEventPayload> envelope,
        CancellationToken ct = default);
}
