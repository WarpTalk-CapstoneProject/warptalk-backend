using Microsoft.EntityFrameworkCore;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Persistence;

namespace WarpTalk.BillingService.Infrastructure.Repositories;

public class CreditLedgerRepository : ICreditLedgerRepository
{
    private readonly BillingDbContext _db;

    public CreditLedgerRepository(BillingDbContext db)
    {
        _db = db;
    }

    public async Task AddAsync(CreditLedgerEntry entry, CancellationToken ct)
    {
        await _db.CreditLedgerEntries.AddAsync(entry, ct);
    }

    public async Task<IReadOnlyList<CreditLedgerEntry>> GetWorkspaceLedgerAsync(
        Guid workspaceId,
        int skip,
        int take,
        CancellationToken ct)
    {
        return await _db.CreditLedgerEntries
            .AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId)
            .OrderByDescending(x => x.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);
    }

    public async Task<decimal> GetWorkspaceBalanceAsync(Guid workspaceId, CancellationToken ct)
    {
        return await _db.CreditLedgerEntries
            .Where(x => x.WorkspaceId == workspaceId)
            .SumAsync(x => x.Amount, ct);
    }

    public async Task<bool> ExistsByIdempotencyKeyAsync(string key, CancellationToken ct)
    {
        return await _db.CreditLedgerEntries
            .AnyAsync(x => x.IdempotencyKey == key, ct);
    }
}