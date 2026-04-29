using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Domain.Interfaces;

public interface ITransactionRepository
{
    Task<Transaction?> GetByOrderCodeAsync(long orderCode, CancellationToken cancellationToken = default);
    Task<IEnumerable<Transaction>> GetByWorkspaceIdAsync(Guid workspaceId, int page = 1, int pageSize = 50, CancellationToken cancellationToken = default);
    Task AddAsync(Transaction transaction, CancellationToken cancellationToken = default);
    Task UpdateAsync(Transaction transaction, CancellationToken cancellationToken = default);
}
