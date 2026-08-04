using System.Data.Common;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Domain.Interfaces;

public interface IUnitOfWork : IDisposable
{
    IPlanRepository Plans { get; }
    ISubscriptionRepository SubscriptionRepository { get; }
    ISubscriptionRepository Subscriptions => SubscriptionRepository;
    ICreditTransactionRepository CreditTransactionRepository { get; }
    ICreditTransactionRepository CreditTransactions => CreditTransactionRepository;
    ICreditBalanceSnapshotRepository CreditBalanceSnapshotRepository { get; }
    IUsageRecordRepository UsageRecordRepository { get; }
    IPaymentRepository PaymentRepository { get; }
    IInvoiceRepository InvoiceRepository { get; }

    ISalesInquiryRepository SalesInquiryRepository { get; }
    IIdempotencyRepository IdempotencyRecords { get; }
    IOutboxMessageRepository OutboxMessages { get; }
    IInboxMessageRepository InboxMessages { get; }

    DbConnection GetDbConnection();
    void ClearTracking();
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
