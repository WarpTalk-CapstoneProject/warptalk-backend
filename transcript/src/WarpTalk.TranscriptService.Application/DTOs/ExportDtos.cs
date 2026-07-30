using System;
using System.Collections.Generic;

namespace WarpTalk.TranscriptService.Application.DTOs;

public record CreateTranscriptExportRequest(
    string Format, // e.g. "txt", "csv"
    IEnumerable<string> IncludedLanguages // e.g. ["en", "vi"]
);

public record TranscriptExportDto(
    Guid Id,
    Guid TranscriptId,
    Guid UserId,
    string Format,
    string FileUrl,
    IEnumerable<string> IncludedLanguages,
    DateTime CreatedAt
);
