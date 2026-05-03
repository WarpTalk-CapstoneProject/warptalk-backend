using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System.Data;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Enums;
using WarpTalk.BillingService.Domain.Interfaces;

namespace WarpTalk.BillingService.Infrastructure.Persistence;

public class BillingDbContext : DbContext, IUnitOfWork
{
    private IDbContextTransaction? _currentTransaction;

    private static readonly DateTime SeedCreatedAt =
        new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public BillingDbContext(DbContextOptions<BillingDbContext> options)
        : base(options) { }

    // ================= DbSets =================

    public DbSet<SubscriptionPlan> SubscriptionPlans => Set<SubscriptionPlan>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<CreditLedgerEntry> CreditLedgerEntries => Set<CreditLedgerEntry>();
    public DbSet<WorkspaceQuotaSnapshot> WorkspaceQuotaSnapshots => Set<WorkspaceQuotaSnapshot>();
    public DbSet<UsageEvent> UsageEvents => Set<UsageEvent>();
    public DbSet<MeetingUsageSession> MeetingUsageSessions => Set<MeetingUsageSession>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<QuotaAuditLog> QuotaAuditLogs => Set<QuotaAuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasDefaultSchema("billing");

        ConfigureSubscriptionPlan(modelBuilder);
        ConfigureSubscription(modelBuilder);
        ConfigureCreditLedgerEntry(modelBuilder);
        ConfigureWorkspaceQuotaSnapshot(modelBuilder);
        ConfigureUsageEvent(modelBuilder);
        ConfigureMeetingUsageSession(modelBuilder);
        ConfigureTransaction(modelBuilder);
        ConfigureQuotaAuditLog(modelBuilder);
    }

    // ===================================================
    // SubscriptionPlan
    // ===================================================
    private static void ConfigureSubscriptionPlan(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SubscriptionPlan>(e =>
        {
            e.ToTable("SubscriptionPlans");

            e.HasKey(x => x.Id);

            e.Property(x => x.Type)
                    .HasConversion<string>()
                    .IsRequired();

            e.Property(x => x.MonthlyPriceVnd).HasColumnType("decimal(18,2)");
            e.Property(x => x.IncludedCredits).HasColumnType("decimal(18,4)");

            e.Property(x => x.VoiceTranslationRatePerHour).HasColumnType("decimal(18,4)");
            e.Property(x => x.TextTranslationRatePerHour).HasColumnType("decimal(18,4)");
            e.Property(x => x.VoiceCloningMultiplier).HasColumnType("decimal(18,4)");
            e.Property(x => x.MultiLanguageStreamMultiplier).HasColumnType("decimal(18,4)");
            e.Property(x => x.AiAssistantMultiplier).HasColumnType("decimal(18,4)");

            e.HasIndex(x => x.Type).IsUnique();

            e.HasData(
                new SubscriptionPlan
                {
                    Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    Type = PlanType.Free,
                    MonthlyPriceVnd = 0,
                    IncludedCredits = 30,
                    VoiceTranslationRatePerHour = 30,
                    TextTranslationRatePerHour = 10,
                    VoiceCloningMultiplier = 1,
                    MultiLanguageStreamMultiplier = 1,
                    AiAssistantMultiplier = 1,
                    MaxParticipants = 5,
                    MaxConcurrentMeetings = 1,
                    MaxLanguagesPerMeeting = 1,
                    SupportsVoiceCloning = false,
                    SupportsAiAssistant = false,
                    SupportsEnterpriseGlossary = false,
                    SupportsMultiLanguageRoom = false,
                    SupportsCreditRollover = false,
                    IsActive = true,
                    CreatedAt = SeedCreatedAt
                }
            );
        });
    }

    // ===================================================
    // Subscription
    // ===================================================
    private static void ConfigureSubscription(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Subscription>(e =>
        {
            e.ToTable("Subscriptions");

            e.HasKey(x => x.Id);

            e.Property(x => x.Status).HasConversion<string>();

            e.Property(x => x.SnapshotMonthlyPriceVnd).HasColumnType("decimal(18,2)");
            e.Property(x => x.SnapshotIncludedCredits).HasColumnType("decimal(18,4)");

            e.HasIndex(x => x.WorkspaceId);
            e.HasIndex(x => x.OwnerUserId);

            e.HasOne<SubscriptionPlan>()
                .WithMany()
                .HasForeignKey(x => x.PlanId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    // ===================================================
    // CreditLedgerEntry
    // ===================================================
    private static void ConfigureCreditLedgerEntry(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CreditLedgerEntry>(e =>
        {
            e.ToTable("CreditLedgerEntries");

            e.HasKey(x => x.Id);

            e.Property(x => x.Type).HasConversion<string>();
            e.Property(x => x.FeatureType).HasConversion<string>();

            e.Property(x => x.Amount).HasColumnType("decimal(18,4)");
            e.Property(x => x.BalanceAfter).HasColumnType("decimal(18,4)");

            e.Property(x => x.IdempotencyKey).HasMaxLength(200);
            e.Property(x => x.MetadataJson).HasColumnType("jsonb");

            e.HasIndex(x => x.WorkspaceId);
            e.HasIndex(x => x.MeetingId);
            e.HasIndex(x => x.IdempotencyKey).IsUnique();
        });
    }

    // ===================================================
    // WorkspaceQuotaSnapshot
    // ===================================================
    private static void ConfigureWorkspaceQuotaSnapshot(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WorkspaceQuotaSnapshot>(e =>
        {
            e.ToTable("WorkspaceQuotaSnapshots");

            e.HasKey(x => x.Id);

            e.Property(x => x.CurrentBalance).HasColumnType("decimal(18,4)");
            e.Property(x => x.ReservedCredits).HasColumnType("decimal(18,4)");
            e.Property(x => x.CurrentMode).HasConversion<string>();

            e.HasIndex(x => x.WorkspaceId).IsUnique();
        });
    }

    // ===================================================
    // UsageEvent
    // ===================================================
    private static void ConfigureUsageEvent(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UsageEvent>(e =>
        {
            e.ToTable("UsageEvents");

            e.HasKey(x => x.Id);

            e.Property(x => x.FeatureType).HasConversion<string>();
            e.Property(x => x.Status).HasConversion<string>();

            e.Property(x => x.CalculatedCredits).HasColumnType("decimal(18,4)");
            e.Property(x => x.IdempotencyKey).HasMaxLength(200);

            e.HasIndex(x => x.WorkspaceId);
            e.HasIndex(x => x.MeetingId);
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.IdempotencyKey).IsUnique();
        });
    }

    // ===================================================
    // MeetingUsageSession
    // ===================================================
    private static void ConfigureMeetingUsageSession(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MeetingUsageSession>(e =>
        {
            e.ToTable("MeetingUsageSessions");

            e.HasKey(x => x.Id);

            e.Property(x => x.QuotaMode).HasConversion<string>();
            e.Property(x => x.Status).HasConversion<string>();

            e.HasIndex(x => x.WorkspaceId);
            e.HasIndex(x => x.MeetingId).IsUnique();
            e.HasIndex(x => x.HostUserId);
        });
    }

    // ===================================================
    // Transaction
    // ===================================================
    private static void ConfigureTransaction(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Transaction>(e =>
        {
            e.ToTable("Transactions");

            e.HasKey(x => x.Id);

            e.Property(x => x.Type).HasConversion<string>();
            e.Property(x => x.Status).HasConversion<string>();

            e.Property(x => x.AmountVnd).HasColumnType("decimal(18,2)");

            e.Property(x => x.IdempotencyKey).HasMaxLength(200);

            e.HasIndex(x => x.OrderCode).IsUnique();
            e.HasIndex(x => x.WorkspaceId);
            e.HasIndex(x => x.OwnerUserId);
            e.HasIndex(x => x.IdempotencyKey).IsUnique();

            e.HasOne<SubscriptionPlan>()
                .WithMany()
                .HasForeignKey(x => x.PlanId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    // ===================================================
    // QuotaAuditLog
    // ===================================================
    private static void ConfigureQuotaAuditLog(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<QuotaAuditLog>(e =>
        {
            e.ToTable("QuotaAuditLogs");

            e.HasKey(x => x.Id);

            e.Property(x => x.Action).HasConversion<string>();
            e.Property(x => x.Description).HasMaxLength(1000);
            e.Property(x => x.MetadataJson).HasColumnType("jsonb");

            e.HasIndex(x => x.WorkspaceId);
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => x.MeetingId);
        });
    }

    // ================= Unit of Work =================

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        => await base.SaveChangesAsync(cancellationToken);

    public async Task BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        if (_currentTransaction != null) return;
        _currentTransaction = await Database.BeginTransactionAsync(cancellationToken);
    }

    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        if (_currentTransaction == null) return;

        await _currentTransaction.CommitAsync(cancellationToken);
        await _currentTransaction.DisposeAsync();
        _currentTransaction = null;
    }

    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        if (_currentTransaction == null) return;

        await _currentTransaction.RollbackAsync(cancellationToken);
        await _currentTransaction.DisposeAsync();
        _currentTransaction = null;
    }

    public IDbTransaction? GetCurrentTransaction()
        => _currentTransaction?.GetDbTransaction();
}