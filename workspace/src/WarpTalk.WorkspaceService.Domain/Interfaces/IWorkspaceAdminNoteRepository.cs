using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.WorkspaceService.Domain.Entities;

namespace WarpTalk.WorkspaceService.Domain.Interfaces;

/// <summary>
/// Append-and-read access to a workspace's internal admin notes. No update or delete, and the
/// runtime role is granted neither (migration 20260924090000).
/// </summary>
public interface IWorkspaceAdminNoteRepository
{
    Task AppendAsync(WorkspaceAdminNote note, CancellationToken ct = default);

    /// <summary>Newest first, at most <paramref name="limit"/>.</summary>
    Task<List<WorkspaceAdminNote>> GetForWorkspaceAsync(Guid workspaceId, int limit, CancellationToken ct = default);
}
