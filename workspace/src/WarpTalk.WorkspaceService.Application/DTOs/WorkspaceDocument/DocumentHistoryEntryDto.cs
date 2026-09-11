using System;

namespace WarpTalk.WorkspaceService.Application.DTOs.WorkspaceDocument;

/// <summary>
/// One entry in a document's approval and feedback history. WT-633.
/// </summary>
/// <remarks>
/// NOTHING NEW IS RECORDED TO SERVE THIS. `workspace_document_audit` has carried the whole
/// lifecycle since the table existed — upload, approve, reject, re-upload, archive, restore, every
/// policy change — and <c>IWorkspaceDocumentAuditRepository.GetPagedAuditsAsync</c> was written to
/// page over it. It had zero callers repo-wide. The history the ticket asks for was already on
/// disk with no door to it; this DTO and its endpoint are the door.
///
/// The one thing genuinely missing was the reject Metadata, which the audit call never passed.
/// </remarks>
/// <param name="Action">
/// The raw audit action — UploadDocument, ApproveDocument, RejectDocument, Reuploaded, and so on.
/// Not translated here: the web owns the labels, and a server-side display string would be a
/// second vocabulary to keep in step with the first.
/// </param>
/// <param name="Reason">
/// The reviewer's rejection reason or the uploader's revision note, lifted out of the audit row's
/// Metadata JSON. Null for every action that carries neither.
/// </param>
public record DocumentHistoryEntryDto(
    Guid Id,
    string Action,
    Guid? ActorId,
    DateTime ActionAt,
    string? Reason
);
