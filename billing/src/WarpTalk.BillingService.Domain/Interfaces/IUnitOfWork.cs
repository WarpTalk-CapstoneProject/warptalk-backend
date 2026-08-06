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

    /// <summary>WT-263: the workspace self-service layer of the entitlement resolution order.</summary>
    IWorkspaceEntitlementOverrideRepository WorkspaceEntitlementOverrides { get; }

    // This interface deliberately exposes no way to reach the raw database
    // connection. Doing so pulled a data-provider dependency into the Domain layer
    // and let any caller bypass the repositories with hand-written SQL. The two
    // operations that genuinely need raw SQL (UsageSettlementRepository,
    // OutboxClaimStore) take BillingDbContext directly in the Infrastructure
    // layer, where that dependency belongs.
    void ClearTracking();
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
