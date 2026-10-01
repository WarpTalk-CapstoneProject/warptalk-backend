using System.Globalization;
using Grpc.Core;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Protos;

namespace WarpTalk.AssistantService.API.GrpcServices;

/// <summary>
/// meet_conference.proto: Google Meet REST through a user's own Google grant, for
/// TranslationRoomService (conference-end detection) and TranscriptService (post-meeting relabel).
/// Internal-token authenticated like every other internal gRPC server (AddWarpTalkGrpcServer).
/// Provider trouble is reported in band through error_code; RpcException is for bad requests.
/// </summary>
public sealed class MeetConferenceGrpcService : MeetConferenceService.MeetConferenceServiceBase
{
    private readonly IHostMeetConferenceService _meet;

    public MeetConferenceGrpcService(IHostMeetConferenceService meet)
    {
        _meet = meet;
    }

    public override async Task<GetConferenceRecordsResponse> GetConferenceRecords(
        MeetConferenceLookupRequest request, ServerCallContext context)
    {
        var result = await _meet.GetConferenceRecordsAsync(
            ParseUser(request.UserId), ParseWorkspace(request.WorkspaceId), request.Meeting, context.CancellationToken);
        var response = new GetConferenceRecordsResponse();
        if (!result.IsSuccess)
        {
            response.ErrorCode = result.ErrorCode ?? MeetConferenceErrorCodes.ProviderUnavailable;
            response.Error = result.Error ?? string.Empty;
            return response;
        }

        response.Records.AddRange(result.Value!.Select(r => new MeetConferenceRecord
        {
            Name = r.Name,
            StartTime = Format(r.StartTime),
            EndTime = Format(r.EndTime),
        }));
        return response;
    }

    public override async Task<ListMeetParticipantsResponse> ListMeetParticipants(
        MeetConferenceLookupRequest request, ServerCallContext context)
    {
        var result = await _meet.GetRosterAsync(
            ParseUser(request.UserId), ParseWorkspace(request.WorkspaceId), request.Meeting, context.CancellationToken);
        var response = new ListMeetParticipantsResponse();
        if (!result.IsSuccess)
        {
            response.ErrorCode = result.ErrorCode ?? MeetConferenceErrorCodes.ProviderUnavailable;
            response.Error = result.Error ?? string.Empty;
            return response;
        }

        response.ConferenceRecord = result.Value!.ConferenceRecord;
        response.Participants.AddRange(result.Value.Participants.Select(p => new MeetParticipant
        {
            Key = p.Key,
            DisplayName = p.DisplayName,
            Kind = p.Kind,
            JoinedAt = Format(p.JoinedAt),
            LeftAt = Format(p.LeftAt),
        }));
        return response;
    }

    public override async Task<GetMeetTranscriptEntriesResponse> GetMeetTranscriptEntries(
        GetMeetTranscriptEntriesRequest request, ServerCallContext context)
    {
        var windowStart = ParseTime(request.WindowStart, nameof(request.WindowStart));
        var windowEnd = ParseTime(request.WindowEnd, nameof(request.WindowEnd));
        var result = await _meet.GetTranscriptEntriesAsync(
            ParseUser(request.UserId), ParseWorkspace(request.WorkspaceId), request.Meeting, windowStart, windowEnd, context.CancellationToken);

        var response = new GetMeetTranscriptEntriesResponse();
        if (!result.IsSuccess)
        {
            response.ErrorCode = result.ErrorCode ?? MeetConferenceErrorCodes.ProviderUnavailable;
            response.Error = result.Error ?? string.Empty;
            return response;
        }

        var value = result.Value!;
        response.ConferenceRecords = value.ConferenceRecords;
        response.TranscriptsReady = value.TranscriptsReady;
        response.TranscriptsPending = value.TranscriptsPending;
        response.ConferenceLive = value.ConferenceLive;
        response.Entries.AddRange(value.Entries.Select(e => new MeetTranscriptEntry
        {
            ParticipantKey = e.ParticipantKey,
            DisplayName = e.DisplayName,
            Text = e.Text,
            LanguageCode = e.LanguageCode ?? string.Empty,
            StartTime = Format(e.StartTime),
            EndTime = Format(e.EndTime),
        }));
        return response;
    }

    private static Guid ParseUser(string value) =>
        Guid.TryParse(value, out var id) && id != Guid.Empty
            ? id
            : throw new RpcException(new Status(StatusCode.InvalidArgument, "user_id must be a GUID."));

    /// <summary>
    /// Empty is an older caller: passed on as null, which the plugin check refuses as
    /// plugin_not_connected (in band). Present but not a GUID is a malformed request.
    /// </summary>
    public static Guid? ParseWorkspace(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return Guid.TryParse(value, out var id) && id != Guid.Empty
            ? id
            : throw new RpcException(new Status(StatusCode.InvalidArgument, "workspace_id must be a GUID."));
    }

    private static DateTimeOffset ParseTime(string value, string field) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : throw new RpcException(new Status(StatusCode.InvalidArgument, $"{field} must be an RFC 3339 time."));

    private static string Format(DateTimeOffset? value) =>
        value?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) ?? string.Empty;
}
