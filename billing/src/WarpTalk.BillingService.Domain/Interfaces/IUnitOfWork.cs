using System.Data.Common;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Domain.Interfaces;

public interface IUnitOfWork : IDisposable
{
    IPlanRepository PlanRepository { get; }
    IPlanRepository Plans => PlanRepository;
    ISubscriptionRepository SubscriptionRepository { get; }
    ISubscriptionRepository Subscriptions => SubscriptionRepository;
    ICreditTransactionRepository CreditTransactionRepository { get; }
    ICreditTransactionRepository CreditTransactions => CreditTransactionRepository;
    IGenericRepository<CreditBalanceSnapshot> CreditBalanceSnapshotRepository { get; }
    IGenericRepository<UsageRecord> UsageRecordRepository { get; }
    IPaymentRepository PaymentRepository { get; }
    IInvoiceRepository InvoiceRepository { get; }
    IRefundRepository RefundRepository { get; }
    IIdempotencyRepository IdempotencyRecords { get; }

    DbConnection GetDbConnection();
    void ClearTracking();
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
