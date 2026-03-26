namespace WarpTalk.TranscriptService.Application.DTOs;

public record CreateTranscriptRequest(
    Guid MeetingId,
    string SourceLanguage
);

public record UpdateTranscriptStatusRequest(
    string Status,
    int TotalSegments,
    int TotalDurationMs
);

public record ProcessAudioChunkRequest(
    string Base64AudioData
);

public record TranscriptDto(
    Guid Id,
    Guid MeetingId,
    int Version,
    string Status,
    string SourceLanguage,
    int TotalSegments,
    int TotalDurationMs,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? FinalizedAt
);
