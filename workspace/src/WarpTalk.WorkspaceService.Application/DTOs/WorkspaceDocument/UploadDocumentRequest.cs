using System;

namespace WarpTalk.WorkspaceService.Application.DTOs.WorkspaceDocument;

public record UploadDocumentRequest(
    string Name,
    string FileName,
    string FileExtension,
    string MimeType,
    long SizeBytes,
    string SourceType,
    Guid? SourceId,
    string? ConfidentialityLevel = null,
    bool IsAiAllowed = true
);
