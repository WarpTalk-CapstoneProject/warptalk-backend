using System;

namespace WarpTalk.WorkspaceService.Application.DTOs.WorkspaceDocument;

public record WorkspaceDocumentDto(
    Guid Id,
    Guid WorkspaceId,
    Guid? UploadedBy,
    Guid? ApprovedBy,
    Guid? OwnerId,
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
    DateTime UpdatedAt,
    /// <summary>
    /// Why the last AI ingestion attempt did not complete, or null when it succeeded, was
    /// skipped, or predates WT-411. WT-409 asked for this: "If security/DLP blocks indexing,
    /// the user/admin should see a specific reason, not only generic AI Failed."
    ///
    /// Trailing and optional so every existing positional construction of this record keeps
    /// compiling — this is a diagnostic addition, not a reshape.
    /// </summary>
    string? IngestionFailureReason = null
);
