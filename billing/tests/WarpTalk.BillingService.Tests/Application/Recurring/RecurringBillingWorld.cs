using System.Linq.Expressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Application.Services.PaymentEventHandlers;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Domain.Services;
using WarpTalk.Shared;
using WarpTalk.Shared.PlatformSettings;

namespace WarpTalk.BillingService.Tests.Application.Recurring;

/// <summary>
/// #466 test harness: an in-memory billing database behind the real PaymentAppService, the real
/// SubscriptionPaymentEventHandler and the real BillingCycleClosingService. SaveChanges enforces
/// the two unique indexes that make a renewal exactly-once in production —
/// payments.provider_transaction_id and credit_transactions.idempotency_key — so a replay that
/// slipped past the short-circuit would fail here the way it fails in Postgres.
/// Nothing talks to Stripe: the gateway is a mock.
/// </summary>
internal sealed class RecurringBillingWorld
{
    public List<Subscription> Subscriptions { get; } = new();
    public List<Plan> Plans { get; } = new();
    public List<Payment> Payments { get; } = new();
    public List<Invoice> Invoices { get; } = new();
    public List<CreditTransaction> Ledger { get; } = new();

    private readonly List<Payment> _pendingPayments = new();
    private readonly List<Invoice> _pendingInvoices = new();
    private readonly List<CreditTransaction> _pendingLedger = new();
    private readonly List<Subscription> _pendingSubscriptions = new();

    public Mock<IUnitOfWork> UnitOfWork { get; } = new();
    public Mock<ISubscriptionRepository> SubscriptionRepository { get; } = new() { CallBase = true };
    public Mock<IPlanRepository> PlanRepository { get; } = new() { CallBase = true };
    public Mock<IPaymentRepository> PaymentRepository { get; } = new() { CallBase = true };
    public Mock<IInvoiceRepository> InvoiceRepository { get; } = new() { CallBase = true };
    public Mock<ICreditTransactionRepository> CreditTransactionRepository { get; } = new() { CallBase = true };
    public Mock<IStripeRecurringGateway> Stripe { get; } = new();
    public Mock<INotificationClient> Notifications { get; } = new();
    public Mock<IPlatformSettings> Settings { get; } = new();
    public int GraceDays { get; set; } = 7;
    public int Saves { get; private set; }

    public RecurringBillingWorld()
    {
        UnitOfWork.Setup(u => u.SubscriptionRepository).Returns(SubscriptionRepository.Object);
        UnitOfWork.Setup(u => u.Plans).Returns(PlanRepository.Object);
        UnitOfWork.Setup(u => u.PaymentRepository).Returns(PaymentRepository.Object);
        UnitOfWork.Setup(u => u.InvoiceRepository).Returns(InvoiceRepository.Object);
        UnitOfWork.Setup(u => u.CreditTransactionRepository).Returns(CreditTransactionRepository.Object);
        UnitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => Save());

        Wire(SubscriptionRepository, Subscriptions, _pendingSubscriptions);
        Wire(PlanRepository, Plans, new List<Plan>());
        Wire(PaymentRepository, Payments, _pendingPayments);
        Wire(InvoiceRepository, Invoices, _pendingInvoices);
        Wire(CreditTransactionRepository, Ledger, _pendingLedger);

        SubscriptionRepository
            .Setup(r => r.GetByStripeSubscriptionIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) => WithPlan(Subscriptions.FirstOrDefault(s => s.StripeSubscriptionId == id)));
        SubscriptionRepository
            .Setup(r => r.GetDueForRenewalAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime threshold, DateTime lower, CancellationToken _) =>
                Subscriptions.Where(SubscriptionOwnership.DueForCycleClose(threshold, lower).Compile()).Select(s => WithPlan(s)!).ToList());
        SubscriptionRepository
            .Setup(r => r.GetExpiredActiveSubscriptionsAsync(It.IsAny<DateTime>(), It.IsAny<TimeSpan>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime now, TimeSpan lookback, TimeSpan margin, CancellationToken _) =>
                Subscriptions.Where(SubscriptionOwnership.DueForExpiry(now, lookback, margin).Compile()).ToList());

        Settings
            .Setup(s => s.GetInt32Async(SubscriptionConstants.Dunning.GraceDaysKey, It.IsAny<int?>(), It.IsAny<SettingContext>(), It.IsAny<CancellationToken>()))
            .Returns(() => ValueTask.FromResult(GraceDays));
        Notifications
            .Setup(n => n.SendNotificationsAsync(It.IsAny<SendBillingNotificationsRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        Stripe.Setup(s => s.IsConfigured).Returns(true);
        Stripe.Setup(s => s.CancelNowAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Result.Success());
    }

    private Subscription? WithPlan(Subscription? s)
    {
        if (s is not null)
        {
            s.Plan ??= Plans.FirstOrDefault(p => p.Id == s.PlanId)!;
        }

        return s;
    }

    private static void Wire<TEntity, TRepo>(Mock<TRepo> repo, List<TEntity> committed, List<TEntity> pending)
        where TEntity : class
        where TRepo : class, IGenericRepository<TEntity>
    {
        IEnumerable<TEntity> All() => committed.Concat(pending);
        repo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<TEntity, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Expression<Func<TEntity, bool>> p, string _, CancellationToken _) => All().FirstOrDefault(p.Compile()));
        repo.Setup(r => r.FindAsync(It.IsAny<Expression<Func<TEntity, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Expression<Func<TEntity, bool>> p, string _, CancellationToken _) => All().Where(p.Compile()).ToList());
        repo.Setup(r => r.AnyAsync(It.IsAny<Expression<Func<TEntity, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Expression<Func<TEntity, bool>> p, CancellationToken _) => All().Any(p.Compile()));
        repo.Setup(r => r.AddAsync(It.IsAny<TEntity>(), It.IsAny<CancellationToken>()))
            .Callback<TEntity, CancellationToken>((e, _) => pending.Add(e))
            .Returns(Task.CompletedTask);
        repo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => All().FirstOrDefault(e => (Guid)typeof(TEntity).GetProperty("Id")!.GetValue(e)! == id));
    }

    private int Save()
    {
        // The two unique indexes a renewal's exactly-once rests on.
        foreach (var payment in _pendingPayments)
        {
            if (Payments.Any(p => p.ProviderTransactionId == payment.ProviderTransactionId))
            {
                Rollback();
                throw new InvalidOperationException($"duplicate key value violates unique constraint \"payments_provider_transaction_id_key\" ({payment.ProviderTransactionId})");
            }
        }

        foreach (var tx in _pendingLedger.Where(t => t.IdempotencyKey is not null))
        {
            if (Ledger.Any(l => l.IdempotencyKey == tx.IdempotencyKey))
            {
                Rollback();
                throw new InvalidOperationException($"duplicate key value violates unique constraint \"ux_credit_transactions_idempotency_key\" ({tx.IdempotencyKey})");
            }
        }

        var written = _pendingPayments.Count + _pendingInvoices.Count + _pendingLedger.Count + _pendingSubscriptions.Count;
        Payments.AddRange(_pendingPayments);
        Invoices.AddRange(_pendingInvoices);
        Ledger.AddRange(_pendingLedger);
        Subscriptions.AddRange(_pendingSubscriptions);
        Rollback();
        Saves++;
        return Math.Max(written, 1);
    }

    private void Rollback()
    {
        _pendingPayments.Clear();
        _pendingInvoices.Clear();
        _pendingLedger.Clear();
        _pendingSubscriptions.Clear();
    }

    // ---- builders ----------------------------------------------------------------------------

    public Plan AddPlan(int creditsPerCycle = 100_000, int rolloverCap = 0, decimal price = 499_000m, string currency = "VND")
    {
        var plan = new Plan
        {
            Id = Guid.NewGuid(),
            Name = "Startup",
            Slug = "startup",
            Tier = SubscriptionConstants.Tiers.Startup,
            Price = price,
            Currency = currency,
            BillingCycle = SubscriptionConstants.BillingCycles.Monthly,
            CreditsPerCycle = creditsPerCycle,
            RolloverCapCredits = rolloverCap,
            InvoiceTermsDays = 15,
            IsActive = true,
        };
        Plans.Add(plan);
        return plan;
    }

    public Subscription AddSubscription(Plan plan, string renewalMode, DateTime periodEnd, int credits = 5_000, string? stripeSubscriptionId = null)
    {
        var sub = new Subscription
        {
            Id = Guid.NewGuid(),
            WorkspaceId = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            PlanId = plan.Id,
            Plan = plan,
            Status = SubscriptionConstants.SubscriptionStatuses.Active,
            IsActive = true,
            AutoRenew = true,
            RenewalMode = renewalMode,
            StripeSubscriptionId = stripeSubscriptionId,
            StripeCustomerId = stripeSubscriptionId is null ? null : "cus_test_1",
            StripeSubscriptionStatus = stripeSubscriptionId is null ? null : SubscriptionConstants.StripeSubscriptionStatuses.Active,
            CreditsRemaining = credits,
            CurrentPeriodStart = periodEnd.AddMonths(-1),
            CurrentPeriodEnd = periodEnd,
            CreatedAt = periodEnd.AddMonths(-1),
        };
        Subscriptions.Add(sub);
        return sub;
    }

    // ---- the services under test -------------------------------------------------------------

    public SubscriptionPaymentEventHandler Handler() => new(
        UnitOfWork.Object,
        NullLogger<SubscriptionPaymentEventHandler>.Instance,
        creditFreeze: null,
        domainService: new SubscriptionDomainService(),
        recurring: Stripe.Object,
        notifications: Notifications.Object,
        settings: Settings.Object);

    public PaymentAppService PaymentApp(Mock<IStripePaymentService>? stripePayments = null, ICustomerCatalogService? catalog = null) => new(
        (stripePayments ?? new Mock<IStripePaymentService>()).Object,
        UnitOfWork.Object,
        NullLogger<PaymentAppService>.Instance,
        Mock.Of<IBillingMessagePublisher>(),
        new IPaymentEventHandler[] { Handler(), new CancellationPaymentEventHandler() },
        Mock.Of<IWorkspaceClient>(),
        Mock.Of<IUsageRateCardRepository>(),
        catalog: catalog,
        recurring: Stripe.Object);

    public StripeSubscriptionLifecycleService Lifecycle(Mock<IStripePaymentService>? stripePayments = null) => new(
        UnitOfWork.Object,
        Stripe.Object,
        (stripePayments ?? new Mock<IStripePaymentService>()).Object,
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [PaymentConstants.StripeConfigKeys.SuccessUrl] = "https://app.warptalk.test/payment/success" })
            .Build(),
        NullLogger<StripeSubscriptionLifecycleService>.Instance);

    /// <summary>The payment event invoice.paid / invoice.payment_failed turns into (see StripeWebhookService).</summary>
    public static StripePaymentEventRequest RenewalEvent(Subscription sub, string invoiceId, bool paid, DateTime periodStart, DateTime periodEnd) => new(
        StripeSessionId: string.Empty,
        PaymentIntentId: invoiceId,
        Amount: 499_000m,
        Currency: "vnd",
        UserIdStr: sub.UserId.ToString(),
        WorkspaceIdStr: sub.WorkspaceId.ToString(),
        PaymentType: PaymentConstants.PaymentTypes.SubscriptionRenewal,
        Status: paid ? PaymentConstants.PaymentStatuses.Paid : PaymentConstants.PaymentStatuses.Failed,
        FailureReason: paid ? string.Empty : "The card was declined for the renewal (attempt 1).",
        PlanSlug: sub.Plan.Slug,
        BillingCycle: SubscriptionConstants.BillingCycles.Monthly,
        StripeSubscriptionId: sub.StripeSubscriptionId ?? string.Empty,
        PeriodEnd: periodEnd,
        StripeCustomerId: "cus_test_1",
        PeriodStart: periodStart);

    public int GrantedCredits(Subscription sub) =>
        Ledger.Where(t => t.SubscriptionId == sub.Id && t.Type == TransactionConstants.TransactionTypes.TopUp).Sum(t => t.Amount);
}
