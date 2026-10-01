namespace WarpTalk.AssistantService.Application.DTOs;

/// <summary>conferenceRecords/{id}. <see cref="EndTime"/> is null while the conference is live.</summary>
public sealed record MeetConferenceRecordDto(string Name, DateTimeOffset? StartTime, DateTimeOffset? EndTime);

/// <summary>One participant of a conference record.</summary>
/// <param name="Key">The participant resource name, conferenceRecords/{id}/participants/{id}.</param>
/// <param name="Kind">One of <see cref="MeetParticipantKinds"/>.</param>
/// <param name="LeftAt">Null while the participant is still in the call.</param>
public sealed record MeetParticipantDto(
    string Key,
    string DisplayName,
    string Kind,
    DateTimeOffset? JoinedAt,
    DateTimeOffset? LeftAt);

public static class MeetParticipantKinds
{
    public const string SignedIn = "signedin";
    public const string Anonymous = "anonymous";
    public const string Phone = "phone";
}

/// <summary>conferenceRecords/{id}/transcripts/{id} and its state (STARTED, ENDED, FILE_GENERATED).</summary>
public sealed record MeetTranscriptDto(string Name, string State)
{
    /// <summary>
    /// Entries are readable once transcription stopped; FILE_GENERATED additionally means the Docs
    /// file exists. STARTED (or an unknown state) is still being produced.
    /// </summary>
    public bool IsReadable =>
        string.Equals(State, "ENDED", StringComparison.OrdinalIgnoreCase)
        || string.Equals(State, "FILE_GENERATED", StringComparison.OrdinalIgnoreCase);
}

/// <summary>One entry of a Meet transcript; <see cref="Participant"/> is the participant resource name.</summary>
public sealed record MeetTranscriptEntryDto(
    string Name,
    string Participant,
    string Text,
    string? LanguageCode,
    DateTimeOffset? StartTime,
    DateTimeOffset? EndTime);

/// <summary>Why a Meet REST call failed.</summary>
public enum MeetRestErrorKind
{
    /// <summary>401 — the access token is no longer accepted. Refresh and retry once.</summary>
    Unauthorized,

    /// <summary>403 for insufficient scope — the grant lacks meetings.space.readonly.</summary>
    ScopeMissing,

    /// <summary>Any other 403 — the user may not read this resource.</summary>
    PermissionDenied,

    NotFound,
    RateLimited,

    /// <summary>5xx, a network failure, a timeout, or a body that could not be read.</summary>
    Unavailable,

    /// <summary>A resource name that is not one this client is willing to put in a URL.</summary>
    InvalidRequest,
}

/// <summary>The outcome of one Meet REST call: a value, or a typed error.</summary>
public sealed class MeetRestResult<T>
{
    private MeetRestResult(T? value, MeetRestErrorKind? error, string? message)
    {
        Value = value;
        Error = error;
        Message = message;
    }

    public T? Value { get; }

    public MeetRestErrorKind? Error { get; }

    public string? Message { get; }

    public bool IsSuccess => Error is null;

    public static MeetRestResult<T> Success(T value) => new(value, null, null);

    public static MeetRestResult<T> Failure(MeetRestErrorKind error, string? message = null) => new(default, error, message);

    /// <summary>Carries a failure over to another value type.</summary>
    public MeetRestResult<TOther> As<TOther>() => MeetRestResult<TOther>.Failure(Error!.Value, Message);
}

/// <summary>The roster of one conference record.</summary>
public sealed record MeetRosterDto(string ConferenceRecord, IReadOnlyList<MeetParticipantDto> Participants);

/// <summary>One transcript line with its speaker resolved to a display name.</summary>
public sealed record MeetTranscriptLineDto(
    string ParticipantKey,
    string DisplayName,
    string Text,
    string? LanguageCode,
    DateTimeOffset? StartTime,
    DateTimeOffset? EndTime);

/// <summary>Google Meet's transcript for the conferences overlapping a window.</summary>
public sealed record MeetTranscriptEntriesDto(
    int ConferenceRecords,
    int TranscriptsReady,
    int TranscriptsPending,
    bool ConferenceLive,
    IReadOnlyList<MeetTranscriptLineDto> Entries);
