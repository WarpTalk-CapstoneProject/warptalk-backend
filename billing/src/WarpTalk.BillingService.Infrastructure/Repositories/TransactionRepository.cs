using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Persistence;

namespace WarpTalk.BillingService.Infrastructure.Repositories;

public class TransactionRepository : ITransactionRepository
{
    private readonly BillingDbContext _dbContext;

    public TransactionRepository(BillingDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<Transaction?> GetByOrderCodeAsync(long orderCode, CancellationToken cancellationToken = default)
    {
        return await _dbContext.Transactions
            .FirstOrDefaultAsync(t => t.OrderCode == orderCode, cancellationToken);
    }

    public async Task<IEnumerable<Transaction>> GetByWorkspaceIdAsync(Guid workspaceId, int page = 1, int pageSize = 50, CancellationToken cancellationToken = default)
    {
        var safePage = Math.Max(1, page);
        var safePageSize = Math.Clamp(pageSize, 1, 200);

        return await _dbContext.Transactions
            .Where(t => t.WorkspaceId == workspaceId)
            .OrderByDescending(t => t.CreatedAt)
            .Skip((safePage - 1) * safePageSize)
            .Take(safePageSize)
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(Transaction transaction, CancellationToken cancellationToken = default)
    {
        await _dbContext.Transactions.AddAsync(transaction, cancellationToken);
    }

    public Task UpdateAsync(Transaction transaction, CancellationToken cancellationToken = default)
    {
        _dbContext.Transactions.Update(transaction);
        return Task.CompletedTask;
    }
}
