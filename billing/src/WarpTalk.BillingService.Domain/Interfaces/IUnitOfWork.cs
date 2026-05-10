using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Domain.Interfaces;

public interface IUnitOfWork : IDisposable
{
    IGenericRepository<Plan> PlansRepository { get; }
    IGenericRepository<Subscription> SubscriptionsRepository { get; }
    IGenericRepository<Transaction> TransactionsRepository { get; }
    IGenericRepository<TokenTransaction> TokenTransactionsRepository { get; }
    IIdempotencyRepository IdempotencyRecordsRepository { get; }
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
