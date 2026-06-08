using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;
using WarpTalk.WorkspaceService.Domain.Entities;

namespace WarpTalk.WorkspaceService.Application.Evaluators;

public interface IDocumentAccessEvaluator
{
    Task<Result> EvaluateAccessAsync(Guid userId, Guid workspaceId, Guid documentId, string requiredPermission, CancellationToken ct = default);
    Task<Result> EvaluateAccessAsync(
        Guid userId,
        Guid workspaceId,
        WorkspaceDocument document,
        string requiredPermission,
        WorkspaceMember member,
        string roleName,
        IEnumerable<WorkspaceDocumentAccessPolicy> policies,
        CancellationToken ct = default);
    Task<bool> CanManagePoliciesAsync(Guid userId, Guid workspaceId, Guid documentId, CancellationToken ct = default);
}
