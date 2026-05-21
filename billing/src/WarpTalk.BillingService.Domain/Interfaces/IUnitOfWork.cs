using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Domain.Interfaces;

public interface IUnitOfWork : IDisposable
{
    IGenericRepository<Plan> PlanRepository { get; }
    IGenericRepository<Subscription> SubscriptionRepository { get; }
    IGenericRepository<CreditTransaction> CreditTransactionRepository { get; }
    IGenericRepository<CreditBalanceSnapshot> CreditBalanceSnapshotRepository { get; }
    IGenericRepository<UsageRecord> UsageRecordRepository { get; }
    IGenericRepository<Payment> PaymentRepository { get; }
    IGenericRepository<Refund> RefundRepository { get; }
    IGenericRepository<Invoice> InvoiceRepository { get; }
    IGenericRepository<SchemaMigration> SchemaMigrationRepository { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
