using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Exceptions;
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
        SubscriptionRepository = new SubscriptionRepository(db);
        CreditTransactionRepository = new CreditTransactionRepository(db);
        CreditBalanceSnapshotRepository = new CreditBalanceSnapshotRepository(db);
        UsageRecordRepository = new GenericRepository<UsageRecord>(db);
        PaymentRepository = new PaymentRepository(db);
        InvoiceRepository = new InvoiceRepository(db);

        SalesInquiryRepository = new GenericRepository<SalesInquiry>(db);
        IdempotencyRecords = new IdempotencyRepository(db);
        OutboxMessages = new GenericRepository<OutboxMessage>(db);
        InboxMessages = new GenericRepository<InboxMessage>(db);
    }

    public IGenericRepository<Plan> Plans { get; }
    public ISubscriptionRepository SubscriptionRepository { get; }
    public ICreditTransactionRepository CreditTransactionRepository { get; }
    public ICreditBalanceSnapshotRepository CreditBalanceSnapshotRepository { get; }
    public IGenericRepository<UsageRecord> UsageRecordRepository { get; }
    public IPaymentRepository PaymentRepository { get; }
    public IInvoiceRepository InvoiceRepository { get; }

    public IGenericRepository<SalesInquiry> SalesInquiryRepository { get; }
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
