namespace WarpTalk.MeetingService.Application.DTOs;

public record CreateMeetingRequest(
    Guid? WorkspaceId,
    string Title,
    string? Description,
    string MeetingType, // e.g., "instant", "scheduled"
    int MaxParticipants,
    string SourceLanguage,
    string TargetLanguages,
    DateTime? ScheduledAt
);

public record JoinMeetingRequest(
    string DisplayName,
    string ListenLanguage,
    string SpeakLanguage
);

public record MeetingDto(
    Guid Id,
    Guid WorkspaceId,
    Guid HostId,
    string Title,
    string? Description,
    string MeetingCode,
    string Status,
    string MeetingType,
    int MaxParticipants,
    DateTime? ScheduledAt,
    DateTime? StartedAt,
    DateTime? EndedAt,
    DateTime CreatedAt
);

public record MeetingParticipantDto(
    Guid Id,
    Guid MeetingId,
    Guid UserId,
    string DisplayName,
    string Role,
    string ListenLanguage,
    string SpeakLanguage,
    string Status,
    DateTime? JoinedAt
);
