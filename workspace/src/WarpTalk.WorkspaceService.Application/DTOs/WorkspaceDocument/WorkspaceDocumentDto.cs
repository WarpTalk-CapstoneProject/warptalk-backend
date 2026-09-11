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
    string? IngestionFailureReason = null,
    /// <summary>
    /// Why a reviewer rejected this document, or null when it was never rejected. WT-633.
    /// </summary>
    /// <remarks>
    /// Read from the latest RejectDocument audit row rather than stored on the document, and only
    /// on the by-id route — the list evaluates N documents already, and this is a detail-page
    /// fact. It is the reason WT-633 exists: the reviewer's words had nowhere to live, so the
    /// uploader was told "rejected" and nothing else.
    ///
    /// It SURVIVES the re-upload that sets the status back to `pending_approval`, because the
    /// audit row it comes from is immutable. That is deliberate: the feedback being answered
    /// should still be readable while the answer is under review. Approval writes its own row, and
    /// the web hides the banner once the status is published.
    /// </remarks>
    string? RejectionReason = null
);
