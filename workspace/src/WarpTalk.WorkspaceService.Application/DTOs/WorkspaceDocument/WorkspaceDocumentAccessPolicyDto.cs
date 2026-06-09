using System;

namespace WarpTalk.WorkspaceService.Application.DTOs.WorkspaceDocument;

public record WorkspaceDocumentAccessPolicyDto(
    Guid Id,
    Guid DocumentId,
    Guid WorkspaceId,
    string SubjectType,
    Guid? SubjectId,
    string? SubjectKey,
    string Permission,
    string Effect,
    DateTime CreatedAt
);
