using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Domain.Interfaces;

public interface IUnitOfWork : IDisposable
{
    IGenericRepository<Plan> Plans { get; }
    IGenericRepository<Subscription> Subscriptions { get; }
    IGenericRepository<Transaction> Transactions { get; }
    IGenericRepository<CreditTransaction> CreditTransactions { get; }
    IIdempotencyRepository IdempotencyRecords { get; }
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
