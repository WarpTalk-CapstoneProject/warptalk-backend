using WarpTalk.TranslationRoomService.Application.Helpers;

namespace WarpTalk.TranslationRoomService.Application.Interfaces;

/// <summary>Records for a Meet code, or why there are none (a WarpTalk.Shared.MeetConferenceErrorCodes value).</summary>
public sealed record MeetConferenceRecordsLookup(string? ErrorCode, IReadOnlyList<MeetConferenceRecordInfo> Records)
{
    public bool IsSuccess => string.IsNullOrEmpty(ErrorCode);
}

/// <summary>
/// Google Meet conference records read with a WarpTalk user's own Google grant. AssistantService
/// holds the grant and answers over meet_conference.proto.
/// </summary>
public interface IMeetConferenceRecordsClient
{
    /// <param name="workspaceId">The room's workspace: the user must have the google_meet plugin
    /// connected and usable there, or the answer is <c>plugin_not_connected</c>.</param>
    Task<MeetConferenceRecordsLookup> GetRecordsAsync(Guid userId, Guid workspaceId, string meetingUrl, CancellationToken ct = default);
}
