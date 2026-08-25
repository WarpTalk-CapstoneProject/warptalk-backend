namespace WarpTalk.AssistantService.Infrastructure.Mcp;

public class GoogleWorkspaceApiOptions
{
    public string DriveFilesEndpoint { get; set; } = "https://www.googleapis.com/drive/v3/files";

    public string CalendarEventsEndpointFormat { get; set; } = "https://www.googleapis.com/calendar/v3/calendars/{0}/events";
}
