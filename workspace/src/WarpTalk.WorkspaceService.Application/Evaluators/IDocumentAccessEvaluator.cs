using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Application.Interfaces;

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
        Dictionary<Guid, TranslationRoomDto?>? roomCache = null,
        Dictionary<Guid, List<TranslationRoomParticipantDto>>? participantsCache = null,
        CancellationToken ct = default);
    /// <summary>
    /// The same evaluation with the restricted gate taken out: would this caller be let in if
    /// the document were not restricted? Gates the PII-masked copy, never the original — see
    /// <see cref="DocumentContentAccessDecision"/>.
    /// </summary>
    Task<Result> EvaluateAccessIgnoringRestrictionAsync(Guid userId, Guid workspaceId, Guid documentId, string requiredPermission, CancellationToken ct = default);

    Task<Result> EvaluateAccessIgnoringRestrictionAsync(
        Guid userId,
        Guid workspaceId,
        WorkspaceDocument document,
        string requiredPermission,
        WorkspaceMember member,
        string roleName,
        IEnumerable<WorkspaceDocumentAccessPolicy> policies,
        Dictionary<Guid, TranslationRoomDto?>? roomCache = null,
        Dictionary<Guid, List<TranslationRoomParticipantDto>>? participantsCache = null,
        CancellationToken ct = default);

    Task<bool> CanManagePoliciesAsync(Guid userId, Guid workspaceId, Guid documentId, CancellationToken ct = default);
}
