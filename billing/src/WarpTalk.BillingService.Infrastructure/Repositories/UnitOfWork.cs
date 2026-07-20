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
        Plans = new GenericRepository<Plan>(db);
        Subscriptions = new GenericRepository<Subscription>(db);
        Transactions = new GenericRepository<Transaction>(db);
        CreditTransactions = new GenericRepository<CreditTransaction>(db);
        IdempotencyRecords = new IdempotencyRepository(db);
    }

    public IGenericRepository<Plan> Plans { get; }
    public IGenericRepository<Subscription> Subscriptions { get; }
    public IGenericRepository<Transaction> Transactions { get; }
    public IGenericRepository<CreditTransaction> CreditTransactions { get; }
    public IIdempotencyRepository IdempotencyRecords { get; }

    public async Task<int> SaveChangesAsync(CancellationToken ct = default)
        => await _db.SaveChangesAsync(ct);

    public void Dispose() => _db.Dispose();
}
