namespace WarpTalk.AssistantService.Infrastructure.Mcp;

public class GoogleWorkspaceApiOptions
{
    public string DriveFilesEndpoint { get; set; } = "https://www.googleapis.com/drive/v3/files";

    public int MaxDriveFileBytes { get; set; } = 200_000;

    public int MaxDriveFileCharacters { get; set; } = 12_000;

    public string CalendarEventsEndpointFormat { get; set; } = "https://www.googleapis.com/calendar/v3/calendars/{0}/events";
}
