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

    public async Task<Dictionary<Guid, string>> GetPlanSlugsAsync(
        IReadOnlyCollection<Guid> workspaceIds,
        CancellationToken ct = default)
    {
        if (workspaceIds.Count == 0) return new Dictionary<Guid, string>();
        var ids = workspaceIds.ToList();
        var rows = await _dbSet
            .AsNoTracking()
            .Where(snapshot => ids.Contains(snapshot.WorkspaceId) && snapshot.PlanSlug != null)
            .Select(snapshot => new { snapshot.WorkspaceId, snapshot.PlanSlug })
            .ToListAsync(ct);
        return rows.ToDictionary(row => row.WorkspaceId, row => row.PlanSlug!);
    }
}
