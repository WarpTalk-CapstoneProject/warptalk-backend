using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Application.Interfaces;

/// <summary>
/// Google Meet REST read through one WarpTalk user's own Google grant — in practice the bridge
/// room's host. Resolves the user's Google connection, checks the grant carries
/// <see cref="MeetConferenceErrorCodes.MeetSpaceReadonlyScope"/>, refreshes the access token the
/// same way WarpBot's tool calls do, and retries once on a 401.
///
/// Before any of that the user must have the <c>google_meet</c> plugin installed AND connected, and
/// usable in <c>workspaceId</c> — the bridge room's workspace — or the call fails with
/// <see cref="MeetConferenceErrorCodes.PluginNotConnected"/>. A Google grant obtained through
/// another Google plugin does not count.
/// </summary>
/// <remarks>
/// Failures carry a <see cref="MeetConferenceErrorCodes"/> value in <c>ErrorCode</c>.
/// <paramref name="meeting"/> on every method is a Meet code or a meet.google.com link.
/// </remarks>
public interface IHostMeetConferenceService
{
    Task<Result<IReadOnlyList<MeetConferenceRecordDto>>> GetConferenceRecordsAsync(
        Guid userId, Guid? workspaceId, string meeting, CancellationToken ct = default);

    /// <summary>
    /// The roster of the live conference for the code, or of the most recent one when none is
    /// live. An empty roster (and empty record name) when the code has never been used.
    /// </summary>
    Task<Result<MeetRosterDto>> GetRosterAsync(
        Guid userId, Guid? workspaceId, string meeting, CancellationToken ct = default);

    /// <summary>
    /// Every readable transcript entry of the conference records that overlap
    /// [<paramref name="windowStart"/>, <paramref name="windowEnd"/>], speaker names resolved.
    /// </summary>
    Task<Result<MeetTranscriptEntriesDto>> GetTranscriptEntriesAsync(
        Guid userId, Guid? workspaceId, string meeting, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken ct = default);
}
