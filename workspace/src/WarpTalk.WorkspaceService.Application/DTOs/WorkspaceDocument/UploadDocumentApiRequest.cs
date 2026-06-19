using System;
using Microsoft.AspNetCore.Http;

namespace WarpTalk.WorkspaceService.Application.DTOs.WorkspaceDocument;

/// <summary>
/// API Request DTO for multipart/form-data document uploads.
/// </summary>
public record UploadDocumentApiRequest(
    string Name,
    string SourceType,
    Guid? SourceId,
    bool IsSensitive,
    IFormFile File
);
