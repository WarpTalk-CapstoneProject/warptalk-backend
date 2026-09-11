using System;
using Microsoft.AspNetCore.Http;

namespace WarpTalk.WorkspaceService.Application.DTOs.WorkspaceDocument;

/// <summary>
/// API Request DTO for multipart/form-data document uploads.
/// </summary>
/// <param name="DuplicateStrategy">
/// What to do when this workspace already holds a document with identical bytes. Absent means
/// `reject`: the upload is refused with 409 naming the existing document, so the caller can come
/// back having chosen `skip`, `replace` or `create_new`. WT-666.
///
/// Trailing and optional so every existing positional construction keeps compiling.
/// </param>
public record UploadDocumentApiRequest(
    string Name,
    string SourceType,
    Guid? SourceId,
    string? ConfidentialityLevel,
    IFormFile File,
    bool IsAiAllowed = true,
    string? DuplicateStrategy = null
);
