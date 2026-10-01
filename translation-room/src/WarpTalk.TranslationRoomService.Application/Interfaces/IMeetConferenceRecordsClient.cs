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
    Task<MeetConferenceRecordsLookup> GetRecordsAsync(Guid userId, string meetingUrl, CancellationToken ct = default);
}
