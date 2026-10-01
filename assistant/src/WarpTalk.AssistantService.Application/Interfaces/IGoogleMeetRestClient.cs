using WarpTalk.AssistantService.Application.DTOs;

namespace WarpTalk.AssistantService.Application.Interfaces;

/// <summary>
/// Google Meet REST v2, read-only, with a caller-supplied access token. Every list follows
/// <c>nextPageToken</c> to the end (bounded). Never throws for a provider or network failure: the
/// result carries a <see cref="MeetRestErrorKind"/>.
/// </summary>
/// <remarks>
/// The token is a parameter rather than something this client looks up, so the one place that
/// decides whose grant is used — and refreshes it — is <see cref="IHostMeetConferenceService"/>.
/// </remarks>
public interface IGoogleMeetRestClient
{
    /// <summary><c>conferenceRecords?filter=space.meeting_code="..."</c>; <paramref name="meetCode"/> must be normalized.</summary>
    Task<MeetRestResult<IReadOnlyList<MeetConferenceRecordDto>>> FindConferenceRecordsAsync(
        string accessToken, string meetCode, CancellationToken ct = default);

    Task<MeetRestResult<IReadOnlyList<MeetParticipantDto>>> ListParticipantsAsync(
        string accessToken, string conferenceRecord, CancellationToken ct = default);

    Task<MeetRestResult<IReadOnlyList<MeetTranscriptDto>>> ListTranscriptsAsync(
        string accessToken, string conferenceRecord, CancellationToken ct = default);

    Task<MeetRestResult<IReadOnlyList<MeetTranscriptEntryDto>>> ListTranscriptEntriesAsync(
        string accessToken, string transcript, CancellationToken ct = default);
}
