using Microsoft.EntityFrameworkCore;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Enums;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Persistence;

namespace WarpTalk.BillingService.Infrastructure.Repositories;

public class TransactionRepository : ITransactionRepository
{
    private readonly BillingDbContext _db;

    public TransactionRepository(BillingDbContext db)
    {
        _db = db;
    }

    public async Task<Transaction?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _db.Transactions
            .FirstOrDefaultAsync(x => x.Id == id, ct);
    }

    public async Task<Transaction?> GetByOrderCodeAsync(long orderCode, CancellationToken ct = default)
    {
        return await _db.Transactions
            .FirstOrDefaultAsync(x => x.OrderCode == orderCode, ct);
    }

    public async Task<IReadOnlyList<Transaction>> GetByOwnerUserIdAsync(
        Guid ownerUserId,
        int skip,
        int take,
        CancellationToken ct = default)
    {
        return await _db.Transactions
            .AsNoTracking()
            .Where(x => x.OwnerUserId == ownerUserId)
            .OrderByDescending(x => x.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);
    }

    // ================= FIXED =================
    public async Task<IReadOnlyList<Transaction>> GetByWorkspaceIdAsync(
        Guid workspaceId,
        int skip,
        int take,
        CancellationToken ct = default)
    {
        return await _db.Transactions
            .AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId)
            .OrderByDescending(x => x.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);
    }

    public async Task AddAsync(Transaction entity, CancellationToken ct = default)
    {
        await _db.Transactions.AddAsync(entity, ct);
    }

    public Task UpdateAsync(Transaction entity, CancellationToken ct = default)
    {
        _db.Transactions.Update(entity);
        return Task.CompletedTask;
    }

    public async Task<bool> ExistsByIdempotencyKeyAsync(string key, CancellationToken ct = default)
    {
        return await _db.Transactions.AnyAsync(x => x.IdempotencyKey == key, ct);
    }

    // ================= FIXED =================
    public async Task UpdateStatusAsync(
        Guid transactionId,
        TransactionStatus status,
        CancellationToken ct = default)
    {
        var tx = await _db.Transactions
            .FirstOrDefaultAsync(x => x.Id == transactionId, ct);

        if (tx == null) return;

        tx.Status = status;

        if (status == TransactionStatus.Success || status == TransactionStatus.Failed)
        {
            tx.CompletedAt = DateTime.UtcNow;
        }

        _db.Transactions.Update(tx);
    }
}