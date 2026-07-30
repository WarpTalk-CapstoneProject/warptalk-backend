using Microsoft.EntityFrameworkCore;
using System.Data.Common;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Exceptions;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Persistence.Contexts;

namespace WarpTalk.BillingService.Infrastructure.Repositories;

public class UnitOfWork : IUnitOfWork
{
    private readonly BillingDbContext _db;

    public UnitOfWork(BillingDbContext db)
    {
        _db = db;
        PlanRepository = new GenericRepository<Plan>(db);
        SubscriptionRepository = new GenericRepository<Subscription>(db);
        CreditTransactionRepository = new GenericRepository<CreditTransaction>(db);
        CreditBalanceSnapshotRepository = new GenericRepository<CreditBalanceSnapshot>(db);
        UsageRecordRepository = new GenericRepository<UsageRecord>(db);
        PaymentRepository = new GenericRepository<Payment>(db);
        InvoiceRepository = new GenericRepository<Invoice>(db);
        RefundRepository = new GenericRepository<Refund>(db);
        IdempotencyRecords = new IdempotencyRepository(db);
        OutboxMessages = new GenericRepository<OutboxMessage>(db);
        InboxMessages = new GenericRepository<InboxMessage>(db);
    }

    public IGenericRepository<Plan> PlanRepository { get; }
    public IGenericRepository<Subscription> SubscriptionRepository { get; }
    public IGenericRepository<CreditTransaction> CreditTransactionRepository { get; }
    public IGenericRepository<CreditBalanceSnapshot> CreditBalanceSnapshotRepository { get; }
    public IGenericRepository<UsageRecord> UsageRecordRepository { get; }
    public IGenericRepository<Payment> PaymentRepository { get; }
    public IGenericRepository<Invoice> InvoiceRepository { get; }
    public IGenericRepository<Refund> RefundRepository { get; }
    public IIdempotencyRepository IdempotencyRecords { get; }
    public IGenericRepository<OutboxMessage> OutboxMessages { get; }
    public IGenericRepository<InboxMessage> InboxMessages { get; }

    public DbConnection GetDbConnection() => _db.Database.GetDbConnection();

    public void ClearTracking() => _db.ChangeTracker.Clear();

    public async Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        try
        {
            return await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new ConcurrencyConflictException(
                "The billing record changed while it was being updated.",
                exception);
        }
    }

    public void Dispose() => _db.Dispose();
}
