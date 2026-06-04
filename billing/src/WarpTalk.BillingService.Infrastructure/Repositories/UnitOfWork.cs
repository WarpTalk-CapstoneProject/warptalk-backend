using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Persistence.Contexts;

namespace WarpTalk.BillingService.Infrastructure.Repositories;

public class UnitOfWork : IUnitOfWork
{
    private readonly BillingDbContext _context;

    private IGenericRepository<Plan>? _plans;
    private IGenericRepository<Subscription>? _subscriptions;
    private IGenericRepository<CreditTransaction>? _creditTransactions;
    private IGenericRepository<CreditBalanceSnapshot>? _creditBalanceSnapshots;
    private IGenericRepository<UsageRecord>? _usageRecords;
    private IGenericRepository<Payment>? _payments;

    private IGenericRepository<SchemaMigration>? _schemaMigrations;

    public UnitOfWork(BillingDbContext context)
    {
        _context = context;
    }

    public IGenericRepository<Plan> PlanRepository =>
        _plans ??= new GenericRepository<Plan>(_context);

    public IGenericRepository<Subscription> SubscriptionRepository =>
        _subscriptions ??= new GenericRepository<Subscription>(_context);

    public IGenericRepository<CreditTransaction> CreditTransactionRepository =>
        _creditTransactions ??= new GenericRepository<CreditTransaction>(_context);

    public IGenericRepository<CreditBalanceSnapshot> CreditBalanceSnapshotRepository =>
        _creditBalanceSnapshots ??= new GenericRepository<CreditBalanceSnapshot>(_context);

    public IGenericRepository<UsageRecord> UsageRecordRepository =>
        _usageRecords ??= new GenericRepository<UsageRecord>(_context);

    public IGenericRepository<Payment> PaymentRepository =>
        _payments ??= new GenericRepository<Payment>(_context);



    public IGenericRepository<SchemaMigration> SchemaMigrationRepository =>
        _schemaMigrations ??= new GenericRepository<SchemaMigration>(_context);

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        return await _context.SaveChangesAsync(cancellationToken);
    }

    public void Dispose()
    {
        _context.Dispose();
    }
}
