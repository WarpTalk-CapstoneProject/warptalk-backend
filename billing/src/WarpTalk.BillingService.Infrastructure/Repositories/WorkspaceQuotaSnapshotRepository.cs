// =======================================================
// WorkspaceQuotaSnapshotRepository.cs
// =======================================================

using Microsoft.EntityFrameworkCore;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Persistence;

namespace WarpTalk.BillingService.Infrastructure.Repositories;

public class WorkspaceQuotaSnapshotRepository
    : IWorkspaceQuotaSnapshotRepository
{
    private readonly BillingDbContext _dbContext;

    public WorkspaceQuotaSnapshotRepository(
        BillingDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<WorkspaceQuotaSnapshot?> GetByWorkspaceIdAsync(
        Guid workspaceId,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.WorkspaceQuotaSnapshots
            .FirstOrDefaultAsync(
                x => x.WorkspaceId == workspaceId,
                cancellationToken);
    }

    public async Task AddAsync(
        WorkspaceQuotaSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        await _dbContext.WorkspaceQuotaSnapshots
            .AddAsync(snapshot, cancellationToken);
    }

    public Task UpdateAsync(
        WorkspaceQuotaSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        snapshot.UpdatedAt = DateTime.UtcNow;

        _dbContext.WorkspaceQuotaSnapshots.Update(snapshot);

        return Task.CompletedTask;
    }
}