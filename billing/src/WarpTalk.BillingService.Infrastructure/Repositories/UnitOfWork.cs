using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Persistence;

namespace WarpTalk.BillingService.Infrastructure.Repositories;

public class UnitOfWork : IUnitOfWork
{
    private readonly BillingDbContext _db;

    public UnitOfWork(BillingDbContext db)
    {
        _db = db;
        PlansRepository = new GenericRepository<Plan>(db);
        SubscriptionsRepository = new GenericRepository<Subscription>(db);
        TransactionsRepository = new GenericRepository<Transaction>(db);
        TokenTransactionsRepository = new GenericRepository<TokenTransaction>(db);
        IdempotencyRecordsRepository = new IdempotencyRepository(db);
    }

    public IGenericRepository<Plan> PlansRepository { get; }
    public IGenericRepository<Subscription> SubscriptionsRepository { get; }
    public IGenericRepository<Transaction> TransactionsRepository { get; }
    public IGenericRepository<TokenTransaction> TokenTransactionsRepository { get; }
    public IIdempotencyRepository IdempotencyRecordsRepository { get; }

    public async Task<int> SaveChangesAsync(CancellationToken ct = default)
        => await _db.SaveChangesAsync(ct);

    public void Dispose() => _db.Dispose();
}
