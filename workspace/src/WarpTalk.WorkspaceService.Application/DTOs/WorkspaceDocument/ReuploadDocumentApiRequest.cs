using Microsoft.AspNetCore.Http;

namespace WarpTalk.WorkspaceService.Application.DTOs.WorkspaceDocument;

/// <summary>
/// API Request DTO for replacing a rejected document's file in place. WT-633.
/// </summary>
/// <param name="Name">
/// An optional new display name. Absent leaves the existing one alone — most revisions fix the
/// file, not the title.
/// </param>
/// <param name="Note">
/// What the uploader changed, in their own words. Optional, and stored on the audit row beside the
/// reviewer's rejection reason so the two halves of the conversation sit in one place.
/// </param>
public record ReuploadDocumentApiRequest(
    IFormFile File,
    string? Name = null,
    string? Note = null
);
