using Microsoft.EntityFrameworkCore;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Persistence;

namespace WarpTalk.BillingService.Infrastructure.Repositories;

public class WorkspaceEntitlementOverrideRepository
    : GenericRepository<WorkspaceEntitlementOverride>, IWorkspaceEntitlementOverrideRepository
{
    private readonly BillingDbContext _db;

    public WorkspaceEntitlementOverrideRepository(BillingDbContext db) : base(db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<WorkspaceEntitlementOverride>> GetForWorkspaceAsync(
        Guid workspaceId,
        CancellationToken ct = default)
        => await _db.WorkspaceEntitlementOverrides
            .Where(row => row.WorkspaceId == workspaceId)
            .ToListAsync(ct);

    public Task<WorkspaceEntitlementOverride?> GetAsync(
        Guid workspaceId,
        string entitlementKey,
        CancellationToken ct = default)
        => _db.WorkspaceEntitlementOverrides
            .FirstOrDefaultAsync(row => row.WorkspaceId == workspaceId && row.EntitlementKey == entitlementKey, ct);
}
