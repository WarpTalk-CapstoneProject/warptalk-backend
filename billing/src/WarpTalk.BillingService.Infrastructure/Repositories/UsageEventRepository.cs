using Microsoft.EntityFrameworkCore;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Enums;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Persistence;

namespace WarpTalk.BillingService.Infrastructure.Repositories;

public class UsageEventRepository : IUsageEventRepository
{
    private readonly BillingDbContext _db;

    public UsageEventRepository(BillingDbContext db)
    {
        _db = db;
    }

    // ===================================================
    // ADD
    // ===================================================
    public async Task AddAsync(UsageEvent entity, CancellationToken ct = default)
    {
        await _db.UsageEvents.AddAsync(entity, ct);
    }

    // ===================================================
    // GET BY ID
    // ===================================================
    public async Task<UsageEvent?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _db.UsageEvents
            .FirstOrDefaultAsync(x => x.Id == id, ct);
    }

    // ===================================================
    // PENDING EVENTS (FIX NAME)
    // ===================================================
    public async Task<IReadOnlyList<UsageEvent>> GetPendingAsync(int take, CancellationToken ct = default)
    {
        var safeTake = Math.Clamp(take, 1, 500);

        return await _db.UsageEvents
            .AsNoTracking()
            .Where(x => x.Status == UsageEventStatus.Pending)
            .OrderBy(x => x.CreatedAt)
            .Take(safeTake)
            .ToListAsync(ct);
    }

    // ===================================================
    // IDEMPOTENCY CHECK
    // ===================================================
    public async Task<bool> ExistsByIdempotencyKeyAsync(string key, CancellationToken ct = default)
    {
        return await _db.UsageEvents
            .AnyAsync(x => x.IdempotencyKey == key, ct);
    }

    // ===================================================
    // UPDATE (FIX: IMPLEMENT PROPERLY)
    // ===================================================
    public Task UpdateAsync(UsageEvent entity, CancellationToken ct = default)
    {
        _db.UsageEvents.Update(entity);
        return Task.CompletedTask;
    }

    // ===================================================
    // STATUS UPDATE (OPTIONAL - CAN KEEP OR REMOVE)
    // ===================================================
    public async Task UpdateStatusAsync(Guid id, UsageEventStatus status, CancellationToken ct = default)
    {
        var entity = await _db.UsageEvents.FirstOrDefaultAsync(x => x.Id == id, ct);

        if (entity == null) return;

        entity.Status = status;

        _db.UsageEvents.Update(entity);
    }
}