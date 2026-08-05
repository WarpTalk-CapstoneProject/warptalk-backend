using Microsoft.EntityFrameworkCore;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.WorkspaceService.Infrastructure.Persistence;

namespace WarpTalk.WorkspaceService.Infrastructure.Repositories;

public class WorkspaceEntitlementSnapshotRepository
    : GenericRepository<WorkspaceEntitlementSnapshot>, IWorkspaceEntitlementSnapshotRepository
{
    public WorkspaceEntitlementSnapshotRepository(WorkspaceDbContext context) : base(context)
    {
    }

    public Task<WorkspaceEntitlementSnapshot?> GetForWorkspaceAsync(
        Guid workspaceId,
        CancellationToken ct = default)
        => _dbSet
            .AsNoTracking()
            .FirstOrDefaultAsync(snapshot => snapshot.WorkspaceId == workspaceId, ct);
}
