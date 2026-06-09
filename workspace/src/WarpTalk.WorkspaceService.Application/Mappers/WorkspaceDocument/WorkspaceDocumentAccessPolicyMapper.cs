using System;
using WarpTalk.WorkspaceService.Application.DTOs.WorkspaceDocument;
using WarpTalk.WorkspaceService.Domain.Entities;

namespace WarpTalk.WorkspaceService.Application.Mappers;

public static class WorkspaceDocumentAccessPolicyMapper
{
    public static WorkspaceDocumentAccessPolicyDto ToDto(this WorkspaceDocumentAccessPolicy policy)
    {
        return new WorkspaceDocumentAccessPolicyDto(
            policy.Id,
            policy.DocumentId,
            policy.WorkspaceId,
            policy.SubjectType,
            policy.SubjectId,
            policy.SubjectKey,
            policy.Permission,
            policy.Effect,
            policy.CreatedAt
        );
    }

    public static WorkspaceDocumentAccessPolicy ToEntity(this AddAccessPolicyRequest request, Guid documentId, Guid workspaceId, Guid userId, DateTime? utcNow = null)
    {
        var now = utcNow ?? DateTime.UtcNow;
        return new WorkspaceDocumentAccessPolicy
        {
            Id = Guid.NewGuid(),
            DocumentId = documentId,
            WorkspaceId = workspaceId,
            SubjectType = request.SubjectType ?? string.Empty,
            SubjectId = request.SubjectId,
            SubjectKey = request.SubjectKey,
            Permission = request.Permission ?? string.Empty,
            Effect = request.Effect ?? string.Empty,
            CreatedAt = now,
            CreatedBy = userId,
            UpdatedAt = now,
            UpdatedBy = userId
        };
    }
}
