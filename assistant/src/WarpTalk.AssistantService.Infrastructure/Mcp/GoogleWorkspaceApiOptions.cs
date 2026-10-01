namespace WarpTalk.AssistantService.Infrastructure.Mcp;

public class GoogleWorkspaceApiOptions
{
    public string DriveFilesEndpoint { get; set; } = "https://www.googleapis.com/drive/v3/files";

    public int MaxDriveFileBytes { get; set; } = 200_000;

    public int MaxDriveFileCharacters { get; set; } = 12_000;

    public string CalendarEventsEndpointFormat { get; set; } = "https://www.googleapis.com/calendar/v3/calendars/{0}/events";

    /// <summary>
    /// Meet REST v2 spaces.create. The google_meet plugin creates its meeting here (scope
    /// meetings.space.created) rather than as a Calendar event, so connecting only Meet never
    /// writes to the user's Calendar. The space comes back with its meetingUri synchronously.
    /// </summary>
    public string MeetSpacesEndpoint { get; set; } = "https://meet.googleapis.com/v2/spaces";

    /// <summary>
    /// The zone a date-time without one is read in. Google refuses "2026-09-24T10:00:00" with
    /// neither an offset nor a timeZone, and a model told the local date sends exactly that. This
    /// is also the zone WarpBot's confirmation card prints, so the card and the booking agree.
    /// </summary>
    public string DefaultTimeZone { get; set; } = "Asia/Ho_Chi_Minh";
}
