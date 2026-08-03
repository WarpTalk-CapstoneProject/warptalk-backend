using System.Data.Common;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Domain.Interfaces;

public interface IUnitOfWork : IDisposable
{
    IGenericRepository<Plan> Plans { get; }
    ISubscriptionRepository SubscriptionRepository { get; }
    ISubscriptionRepository Subscriptions => SubscriptionRepository;
    ICreditTransactionRepository CreditTransactionRepository { get; }
    ICreditTransactionRepository CreditTransactions => CreditTransactionRepository;
    ICreditBalanceSnapshotRepository CreditBalanceSnapshotRepository { get; }
    IGenericRepository<UsageRecord> UsageRecordRepository { get; }
    IPaymentRepository PaymentRepository { get; }
    IInvoiceRepository InvoiceRepository { get; }

    IGenericRepository<SalesInquiry> SalesInquiryRepository { get; }
    IIdempotencyRepository IdempotencyRecords { get; }
    IGenericRepository<OutboxMessage> OutboxMessages { get; }
    IGenericRepository<InboxMessage> InboxMessages { get; }

    DbConnection GetDbConnection();
    void ClearTracking();
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
