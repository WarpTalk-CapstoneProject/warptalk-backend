using Microsoft.EntityFrameworkCore;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Enums;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Persistence;

namespace WarpTalk.BillingService.Infrastructure.Repositories;

public class SubscriptionRepository : ISubscriptionRepository
{
    private readonly BillingDbContext _db;

    public SubscriptionRepository(BillingDbContext db)
    {
        _db = db;
    }

    // ===================================================
    // GET BY ID
    // ===================================================
    public async Task<Subscription?> GetByIdAsync(
        Guid id,
        CancellationToken ct = default)
    {
        return await _db.Subscriptions
            .FirstOrDefaultAsync(x => x.Id == id, ct);
    }

    // ===================================================
    // ACTIVE SUBSCRIPTION BY WORKSPACE
    // ===================================================
    public async Task<Subscription?> GetActiveByWorkspaceIdAsync(
        Guid workspaceId,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        return await _db.Subscriptions
            .FirstOrDefaultAsync(x =>
                x.WorkspaceId == workspaceId &&
                x.Status == SubscriptionStatus.Active &&
                x.StartDate <= now &&
                x.EndDate >= now,
                ct);
    }

    // ===================================================
    // GET ALL BY WORKSPACE
    // ===================================================
    public async Task<IReadOnlyList<Subscription>> GetByWorkspaceIdAsync(
        Guid workspaceId,
        CancellationToken ct = default)
    {
        return await _db.Subscriptions
            .AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(ct);
    }

    // ===================================================
    // ADD
    // ===================================================
    public async Task AddAsync(
        Subscription entity,
        CancellationToken ct = default)
    {
        await _db.Subscriptions.AddAsync(entity, ct);
    }

    // ===================================================
    // UPDATE
    // ===================================================
    public Task UpdateAsync(
        Subscription entity,
        CancellationToken ct = default)
    {
        entity.UpdatedAt = DateTime.UtcNow;
        _db.Subscriptions.Update(entity);
        return Task.CompletedTask;
    }

    // ===================================================
    // INTERFACE EXPLICIT IMPLEMENTATION (FIXED)
    // ===================================================
    async Task<IReadOnlyList<Subscription>> ISubscriptionRepository.GetByOwnerUserIdAsync(
        Guid ownerUserId,
        CancellationToken cancellationToken)
    {
        return await _db.Subscriptions
            .AsNoTracking()
            .Where(x => x.OwnerUserId == ownerUserId)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    async Task<Subscription?> ISubscriptionRepository.GetActiveByWorkspaceIdAsync(
        Guid workspaceId,
        CancellationToken cancellationToken)
    {
        return await GetActiveByWorkspaceIdAsync(workspaceId, cancellationToken);
    }

    async Task<IReadOnlyList<Subscription>> ISubscriptionRepository.GetByWorkspaceIdAsync(
        Guid workspaceId,
        CancellationToken cancellationToken)
    {
        return await GetByWorkspaceIdAsync(workspaceId, cancellationToken);
    }
}