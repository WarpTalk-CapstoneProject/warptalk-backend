using WarpTalk.MeetingService.Application.DTOs;

namespace WarpTalk.MeetingService.Application.Interfaces;

/// <summary>
/// Hands a "somebody left a room that is being recorded" fact to whatever watches for the end of a
/// Google Meet bridge session (BridgeRecordingEndWorker in the API host). Must return at once and
/// never throw: it is called from inside the LiveKit webhook, whose real work has already succeeded.
/// </summary>
public interface IBridgeRecordingEndWatcher
{
    void NotifyParticipantLeft(BridgeRecordingEndRequest request);
}
