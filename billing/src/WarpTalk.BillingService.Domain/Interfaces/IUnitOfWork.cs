using System.Data.Common;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Domain.Interfaces;

public interface IUnitOfWork : IDisposable
{
    IGenericRepository<Plan> PlanRepository { get; }
    IGenericRepository<Plan> Plans => PlanRepository;
    IGenericRepository<Subscription> SubscriptionRepository { get; }
    IGenericRepository<Subscription> Subscriptions => SubscriptionRepository;
    IGenericRepository<CreditTransaction> CreditTransactionRepository { get; }
    IGenericRepository<CreditTransaction> CreditTransactions => CreditTransactionRepository;
    IGenericRepository<CreditBalanceSnapshot> CreditBalanceSnapshotRepository { get; }
    IGenericRepository<UsageRecord> UsageRecordRepository { get; }
    IGenericRepository<Payment> PaymentRepository { get; }
    IGenericRepository<Invoice> InvoiceRepository { get; }
    IGenericRepository<Refund> RefundRepository { get; }
    IIdempotencyRepository IdempotencyRecords { get; }

    DbConnection GetDbConnection();
    void ClearTracking();
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
