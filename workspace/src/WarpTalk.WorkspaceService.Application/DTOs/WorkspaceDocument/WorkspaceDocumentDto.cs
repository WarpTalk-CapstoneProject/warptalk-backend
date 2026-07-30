using System;

namespace WarpTalk.WorkspaceService.Application.DTOs.WorkspaceDocument;

public record WorkspaceDocumentDto(
    Guid Id,
    Guid WorkspaceId,
    Guid? UploadedBy,
    Guid? OwnerId,
    Guid? ApprovedBy,
    string Name,
    string FileName,
    string FileExtension,
    string MimeType,
    long SizeBytes,
    string SourceType,
    Guid? SourceId,
    string IngestionStatus,
    bool AiEligible,
    bool IsAiAllowed,
    string ConfidentialityLevel,
    string RetentionState,
    string Status,
    string? DownloadUrl,
    DateTime CreatedAt,
    DateTime UpdatedAt
);
