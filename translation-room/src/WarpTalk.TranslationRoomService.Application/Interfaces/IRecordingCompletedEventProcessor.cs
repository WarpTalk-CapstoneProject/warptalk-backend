using WarpTalk.Shared;
using WarpTalk.Shared.Events;

namespace WarpTalk.TranslationRoomService.Application.Interfaces;

public interface IRecordingCompletedEventProcessor
{
    Task<Result<bool>> ProcessAsync(
        EventEnvelope<MeetingRecordingCompletedEventPayload> envelope,
        CancellationToken ct = default);
}
