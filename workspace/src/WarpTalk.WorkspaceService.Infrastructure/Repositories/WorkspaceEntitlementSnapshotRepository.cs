using Microsoft.EntityFrameworkCore;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.WorkspaceService.Infrastructure.Persistence;

namespace WarpTalk.WorkspaceService.Infrastructure.Repositories;

public class WorkspaceEntitlementSnapshotRepository
    : GenericRepository<WorkspaceEntitlementSnapshot>, IWorkspaceEntitlementSnapshotRepository
{
    private readonly WorkspaceDbContext _context;

    public WorkspaceEntitlementSnapshotRepository(WorkspaceDbContext context) : base(context)
    {
        _context = context;
    }

    public Task<WorkspaceEntitlementSnapshot?> GetForWorkspaceAsync(
        Guid workspaceId,
        CancellationToken ct = default)
        => _context.WorkspaceEntitlementSnapshots
            .AsNoTracking()
            .FirstOrDefaultAsync(snapshot => snapshot.WorkspaceId == workspaceId, ct);
}
