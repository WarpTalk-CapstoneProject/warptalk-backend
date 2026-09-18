namespace WarpTalk.AssistantService.Infrastructure.Mcp;

public class GoogleWorkspaceApiOptions
{
    public string DriveFilesEndpoint { get; set; } = "https://www.googleapis.com/drive/v3/files";

    public int MaxDriveFileBytes { get; set; } = 200_000;

    public int MaxDriveFileCharacters { get; set; } = 12_000;

    public string CalendarEventsEndpointFormat { get; set; } = "https://www.googleapis.com/calendar/v3/calendars/{0}/events";

    /// <summary>
    /// Google attaches a Meet conference asynchronously: the insert can answer with
    /// conferenceData.createRequest.status "pending" and no link yet. The gateway re-reads the
    /// event this many times, this far apart, before giving up and reporting the link as pending.
    /// </summary>
    public int MeetConferencePollAttempts { get; set; } = 3;

    public int MeetConferencePollDelayMilliseconds { get; set; } = 700;
}
