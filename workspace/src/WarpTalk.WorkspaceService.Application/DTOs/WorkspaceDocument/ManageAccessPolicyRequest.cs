using System;

namespace WarpTalk.WorkspaceService.Application.DTOs.WorkspaceDocument;

public record ManageAccessPolicyRequest(
    string Action, // "Add", "Remove"
    Guid? PolicyId,
    string? SubjectType,
    Guid? SubjectId,
    string? SubjectKey,
    string? Permission,
    string? Effect
);
