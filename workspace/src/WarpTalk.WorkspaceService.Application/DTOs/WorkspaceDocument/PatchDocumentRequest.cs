using System;

namespace WarpTalk.WorkspaceService.Application.DTOs.WorkspaceDocument;

public record PatchDocumentRequest(
    string? Name,
    string? ConfidentialityLevel,
    bool? IsAiAllowed
);
