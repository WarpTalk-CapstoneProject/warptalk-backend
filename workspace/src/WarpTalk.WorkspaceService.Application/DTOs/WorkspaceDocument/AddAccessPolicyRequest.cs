using System;

namespace WarpTalk.WorkspaceService.Application.DTOs.WorkspaceDocument;

public record AddAccessPolicyRequest(
    string SubjectType,
    Guid? SubjectId,
    string? SubjectKey,
    string? Permission,
    string? Effect
);
