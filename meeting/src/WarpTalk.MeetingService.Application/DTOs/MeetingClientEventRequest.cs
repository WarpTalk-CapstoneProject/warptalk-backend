namespace WarpTalk.MeetingService.Application.DTOs;

/// <summary>
/// What a browser reports about its own LiveKit connection. See MeetingClientEventsController.
/// </summary>
public sealed record MeetingClientEventRequest(
    string Kind,
    string? Code = null,
    string? Message = null,
    int? ElapsedMs = null,
    int? Attempt = null,
    string? UserAgent = null);
