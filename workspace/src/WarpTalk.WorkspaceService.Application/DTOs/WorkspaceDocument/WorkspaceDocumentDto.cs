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
    string? RejectionReason = null,
    /// <summary>
    /// WT-854 — a corrected file waiting for review, or null. Everything above still describes
    /// the APPROVED file, which is what readers, downloads and the AI index use.
    /// </summary>
    WorkspaceDocumentPendingRevisionDto? PendingRevision = null,
    /// <summary>
    /// Which version of the content THIS caller gets: `original`, `masked` or `none`.
    /// </summary>
    /// <remarks>
    /// `masked` is the PII-masked copy of a restricted document — the same format with personal
    /// details replaced by markers — and is all an ordinary member is given. The original
    /// download answers 403 for that caller and <see cref="DownloadUrl"/> is null. `none` is a
    /// document the caller may see listed but has no readable version of yet.
    /// Decided by DocumentContentAccessDecision, the same function the download routes ask.
    /// </remarks>
    string ContentAccess = "original",
    /// <summary>
    /// A masked copy exists AND this caller may read it, at GET documents/{id}/masked/download.
    /// True for a member whose content is the masked copy, and for Owner/Admin and the uploader,
    /// who get it alongside the original so they can see what members see.
    /// </summary>
    bool MaskedVersionAvailable = false,
    /// <summary>
    /// Why the document does or does not have a masked copy — a value of
    /// WorkspaceDocumentMaskedVersionStatuses — or null for a document that is not restricted.
    /// Detail route only.
    /// </summary>
    string? MaskedVersionStatus = null,
    /// <summary>
    /// Whether this caller may ask for the security scan to be run again to produce the masked
    /// copy: Owner/Admin, on a restricted document. Detail route only.
    /// </summary>
    bool CanRescanMaskedVersion = false
);

/// <summary>
/// WT-854 — a corrected file uploaded for a published document, awaiting a reviewer. The
/// document's own fields (and its download, preview and AI index) still describe the approved
/// file; this is what an approval would replace them with. Its bytes are at
/// GET documents/{id}/revision/download, for reviewers and the uploader only.
/// </summary>
public record WorkspaceDocumentPendingRevisionDto(
    string Name,
    string FileName,
    string FileExtension,
    string MimeType,
    long SizeBytes,
    Guid? UploadedBy,
    DateTime UploadedAt,
    string? Note
);
