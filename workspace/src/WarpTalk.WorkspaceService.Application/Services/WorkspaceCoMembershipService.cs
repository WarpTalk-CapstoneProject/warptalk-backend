using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Domain.Interfaces;

namespace WarpTalk.WorkspaceService.Application.Services;

/// <inheritdoc cref="IWorkspaceCoMembershipService"/>
public class WorkspaceCoMembershipService : IWorkspaceCoMembershipService
{
    /// <summary>
    /// Mirrors the Gateway's own cap. Defence in depth, not duplication: the Gateway trims the
    /// request to 500 before calling, but this service is reachable from anywhere on the mesh and
    /// must not be the thing that turns an oversized call into an unbounded query.
    /// </summary>
    private const int MaxCandidates = 500;

    private readonly IUnitOfWork _unitOfWork;

    public WorkspaceCoMembershipService(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<Guid>>> GetVisibleCoMemberIdsAsync(
        Guid callerUserId,
        IReadOnlyCollection<Guid> candidateUserIds,
        CancellationToken ct = default)
    {
        if (callerUserId == Guid.Empty || candidateUserIds.Count == 0)
        {
            return Result.Success<IReadOnlyList<Guid>>(Array.Empty<Guid>());
        }

        var candidates = candidateUserIds.Count > MaxCandidates
            ? new List<Guid>(candidateUserIds).GetRange(0, MaxCandidates)
            : candidateUserIds;

        var visible = await _unitOfWork.WorkspaceMemberRepository
            .GetCoMemberUserIdsAsync(callerUserId, candidates, ct);

        return Result.Success<IReadOnlyList<Guid>>(visible);
    }
}
