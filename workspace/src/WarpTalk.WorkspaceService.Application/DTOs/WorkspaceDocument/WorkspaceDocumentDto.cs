using System;

namespace WarpTalk.WorkspaceService.Application.DTOs.WorkspaceDocument;

public record WorkspaceDocumentDto(
    Guid Id,
    Guid WorkspaceId,
    Guid? UploadedBy,
    Guid? OwnerId,
    string Name,
    string FileName,
    string FileExtension,
    string MimeType,
    long SizeBytes,
    string SourceType,
    Guid? SourceId,
    string IngestionStatus,
    bool IsSensitive,
    string ConfidentialityLevel,
    string RetentionState,
    string Status,
    string? DownloadUrl,
    DateTime CreatedAt,
    DateTime UpdatedAt
);
