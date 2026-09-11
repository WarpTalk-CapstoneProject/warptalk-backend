using System;

namespace WarpTalk.WorkspaceService.Application.DTOs.WorkspaceDocument;

/// <summary>
/// The document whose bytes the caller just tried to upload again.
/// </summary>
/// <remarks>
/// Returned in the 409 body so the upload dialog can say WHICH document it collided with — "this
/// file is already here as 'Q3 Handbook', uploaded 12 Aug" — rather than a bare "duplicate". The
/// three choices WT-666 asks for are only meaningful if the person can see what they are choosing
/// about.
///
/// Deliberately thin. A duplicate collision is answered for any member who may upload, and the
/// existing document may be one they cannot open — its name, status and dates are enough to
/// decide, and nothing here is content.
/// </remarks>
public record DocumentDuplicateDto(
    Guid DocumentId,
    string Name,
    string FileName,
    string Status,
    long SizeBytes,
    DateTime CreatedAt
);
