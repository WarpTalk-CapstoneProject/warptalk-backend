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
        Plans = new PlanRepository(db);
        SubscriptionRepository = new SubscriptionRepository(db);
        CreditTransactionRepository = new CreditTransactionRepository(db);
        CreditBalanceSnapshotRepository = new CreditBalanceSnapshotRepository(db);
        UsageRecordRepository = new UsageRecordRepository(db);
        PaymentRepository = new PaymentRepository(db);
        InvoiceRepository = new InvoiceRepository(db);

        SalesInquiryRepository = new SalesInquiryRepository(db);
        IdempotencyRecords = new IdempotencyRepository(db);
        OutboxMessages = new OutboxMessageRepository(db);
        InboxMessages = new InboxMessageRepository(db);
        WorkspaceEntitlementOverrides = new WorkspaceEntitlementOverrideRepository(db);
    }

    public IPlanRepository Plans { get; }
    public ISubscriptionRepository SubscriptionRepository { get; }
    public ICreditTransactionRepository CreditTransactionRepository { get; }
    public ICreditBalanceSnapshotRepository CreditBalanceSnapshotRepository { get; }
    public IUsageRecordRepository UsageRecordRepository { get; }
    public IPaymentRepository PaymentRepository { get; }
    public IInvoiceRepository InvoiceRepository { get; }

    public ISalesInquiryRepository SalesInquiryRepository { get; }
    public IIdempotencyRepository IdempotencyRecords { get; }
    public IOutboxMessageRepository OutboxMessages { get; }
    public IInboxMessageRepository InboxMessages { get; }
    public IWorkspaceEntitlementOverrideRepository WorkspaceEntitlementOverrides { get; }

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
