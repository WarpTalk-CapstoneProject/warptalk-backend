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
                },
                new SubscriptionPlan
                {
                    Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                    Type = PlanType.Pro,
                    MonthlyPriceVnd = 199000,
                    IncludedCredits = 500,
                    VoiceTranslationRatePerHour = 12,
                    TextTranslationRatePerHour = 4,
                    VoiceCloningMultiplier = 1.5m,
                    MultiLanguageStreamMultiplier = 1.2m,
                    AiAssistantMultiplier = 1.2m,
                    MaxParticipants = 25,
                    MaxConcurrentMeetings = 5,
                    MaxLanguagesPerMeeting = 2,
                    SupportsVoiceCloning = true,
                    SupportsAiAssistant = true,
                    SupportsEnterpriseGlossary = false,
                    SupportsMultiLanguageRoom = true,
                    SupportsCreditRollover = false,
                    IsActive = true,
                    CreatedAt = SeedCreatedAt
                },
                new SubscriptionPlan
                {
                    Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                    Type = PlanType.Premium,
                    MonthlyPriceVnd = 499000,
                    IncludedCredits = 1000,
                    VoiceTranslationRatePerHour = 10,
                    TextTranslationRatePerHour = 3,
                    VoiceCloningMultiplier = 1.3m,
                    MultiLanguageStreamMultiplier = 1.1m,
                    AiAssistantMultiplier = 1.1m,
                    MaxParticipants = 100,
                    MaxConcurrentMeetings = 15,
                    MaxLanguagesPerMeeting = 4,
                    SupportsVoiceCloning = true,
                    SupportsAiAssistant = true,
                    SupportsEnterpriseGlossary = true,
                    SupportsMultiLanguageRoom = true,
                    SupportsCreditRollover = true,
                    IsActive = true,
                    CreatedAt = SeedCreatedAt
                },
                new SubscriptionPlan
                {
                    Id = Guid.Parse("44444444-4444-4444-4444-444444444444"),
                    Type = PlanType.Enterprise,
                    MonthlyPriceVnd = 0,
                    IncludedCredits = 10000,
                    VoiceTranslationRatePerHour = 8,
                    TextTranslationRatePerHour = 2,
                    VoiceCloningMultiplier = 1,
                    MultiLanguageStreamMultiplier = 1,
                    AiAssistantMultiplier = 1,
                    MaxParticipants = 1000,
                    MaxConcurrentMeetings = 100,
                    MaxLanguagesPerMeeting = 10,
                    SupportsVoiceCloning = true,
                    SupportsAiAssistant = true,
                    SupportsEnterpriseGlossary = true,
                    SupportsMultiLanguageRoom = true,
                    SupportsCreditRollover = true,
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

            e.HasData(
                new Subscription
                {
                    Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                    WorkspaceId = Guid.Parse("ffffffff-1111-1111-1111-111111111111"),
                    OwnerUserId = Guid.Parse("dddddddd-1111-1111-1111-111111111111"),
                    PlanId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                    Status = SubscriptionStatus.Active,
                    SnapshotMonthlyPriceVnd = 199000,
                    SnapshotIncludedCredits = 500,
                    AutoRenew = true,
                    StartDate = SeedCreatedAt,
                    EndDate = null,
                    CancelledAt = null,
                    CreatedAt = SeedCreatedAt,
                    UpdatedAt = null
                },
                new Subscription
                {
                    Id = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                    WorkspaceId = Guid.Parse("ffffffff-2222-2222-2222-222222222222"),
                    OwnerUserId = Guid.Parse("eeeeeeee-1111-1111-1111-111111111111"),
                    PlanId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                    Status = SubscriptionStatus.Active,
                    SnapshotMonthlyPriceVnd = 499000,
                    SnapshotIncludedCredits = 1000,
                    AutoRenew = true,
                    StartDate = SeedCreatedAt,
                    EndDate = null,
                    CancelledAt = null,
                    CreatedAt = SeedCreatedAt,
                    UpdatedAt = null
                },
                new Subscription
                {
                    Id = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
                    WorkspaceId = Guid.Parse("ffffffff-3333-3333-3333-333333333333"),
                    OwnerUserId = Guid.Parse("dddddddd-2222-2222-2222-222222222222"),
                    PlanId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    Status = SubscriptionStatus.Active,
                    SnapshotMonthlyPriceVnd = 0,
                    SnapshotIncludedCredits = 30,
                    AutoRenew = false,
                    StartDate = SeedCreatedAt,
                    EndDate = null,
                    CancelledAt = null,
                    CreatedAt = SeedCreatedAt,
                    UpdatedAt = null
                }
            );
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

            e.HasData(
                // Initial subscription credits
                new CreditLedgerEntry
                {
                    Id = Guid.Parse("11110000-0000-0000-0000-000000000001"),
                    WorkspaceId = Guid.Parse("ffffffff-1111-1111-1111-111111111111"),
                    UserId = Guid.Parse("dddddddd-1111-1111-1111-111111111111"),
                    SubscriptionId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                    MeetingId = null,
                    TransactionId = null,
                    Type = LedgerEntryType.Credit,
                    FeatureType = BillingFeatureType.VoiceTranslation,
                    Amount = 500,
                    BalanceAfter = 500,
                    Currency = "CREDIT",
                    IdempotencyKey = "sub-pro-init-001",
                    MetadataJson = null,
                    CreatedAt = SeedCreatedAt
                },
                // Debit from meeting usage
                new CreditLedgerEntry
                {
                    Id = Guid.Parse("11110000-0000-0000-0000-000000000002"),
                    WorkspaceId = Guid.Parse("ffffffff-1111-1111-1111-111111111111"),
                    UserId = Guid.Parse("dddddddd-1111-1111-1111-111111111111"),
                    SubscriptionId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                    MeetingId = Guid.Parse("00000000-1111-1111-1111-111111111111"),
                    TransactionId = null,
                    Type = LedgerEntryType.Debit,
                    FeatureType = BillingFeatureType.VoiceTranslation,
                    Amount = -75.5m,
                    BalanceAfter = 424.5m,
                    Currency = "CREDIT",
                    IdempotencyKey = "meeting-usage-001",
                    MetadataJson = "{\"meeting_duration_ms\": 3600000, \"language_count\": 2}",
                    CreatedAt = new DateTime(2026, 1, 2, 10, 30, 0, DateTimeKind.Utc)
                },
                // Another debit
                new CreditLedgerEntry
                {
                    Id = Guid.Parse("11110000-0000-0000-0000-000000000003"),
                    WorkspaceId = Guid.Parse("ffffffff-1111-1111-1111-111111111111"),
                    UserId = Guid.Parse("dddddddd-1111-1111-1111-111111111111"),
                    SubscriptionId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                    MeetingId = Guid.Parse("00000000-2222-2222-2222-222222222222"),
                    TransactionId = null,
                    Type = LedgerEntryType.Debit,
                    FeatureType = BillingFeatureType.VoiceCloning,
                    Amount = -50m,
                    BalanceAfter = 374.5m,
                    Currency = "CREDIT",
                    IdempotencyKey = "voice-cloning-usage-001",
                    MetadataJson = "{\"voice_clone_count\": 2}",
                    CreatedAt = new DateTime(2026, 1, 3, 14, 15, 0, DateTimeKind.Utc)
                },
                // Refund
                new CreditLedgerEntry
                {
                    Id = Guid.Parse("11110000-0000-0000-0000-000000000004"),
                    WorkspaceId = Guid.Parse("ffffffff-1111-1111-1111-111111111111"),
                    UserId = Guid.Parse("dddddddd-1111-1111-1111-111111111111"),
                    SubscriptionId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                    MeetingId = null,
                    TransactionId = null,
                    Type = LedgerEntryType.Refund,
                    FeatureType = BillingFeatureType.VoiceTranslation,
                    Amount = 10m,
                    BalanceAfter = 384.5m,
                    Currency = "CREDIT",
                    IdempotencyKey = "refund-failed-meeting-001",
                    MetadataJson = "{\"reason\": \"Failed transaction\"}",
                    CreatedAt = new DateTime(2026, 1, 4, 9, 0, 0, DateTimeKind.Utc)
                },
                // Premium workspace initial credits
                new CreditLedgerEntry
                {
                    Id = Guid.Parse("11110000-0000-0000-0000-000000000005"),
                    WorkspaceId = Guid.Parse("ffffffff-2222-2222-2222-222222222222"),
                    UserId = Guid.Parse("eeeeeeee-1111-1111-1111-111111111111"),
                    SubscriptionId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                    MeetingId = null,
                    TransactionId = null,
                    Type = LedgerEntryType.Credit,
                    FeatureType = BillingFeatureType.VoiceTranslation,
                    Amount = 1000,
                    BalanceAfter = 1000,
                    Currency = "CREDIT",
                    IdempotencyKey = "sub-premium-init-001",
                    MetadataJson = null,
                    CreatedAt = SeedCreatedAt
                }
            );
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

            e.HasData(
                new WorkspaceQuotaSnapshot
                {
                    Id = Guid.Parse("22220000-0000-0000-0000-000000000001"),
                    WorkspaceId = Guid.Parse("ffffffff-1111-1111-1111-111111111111"),
                    CurrentBalance = 384.5m,
                    ReservedCredits = 50m,
                    LowCreditThreshold = 50m,
                    CurrentMode = QuotaMode.FullVoice,
                    UpdatedAt = new DateTime(2026, 1, 4, 9, 0, 0, DateTimeKind.Utc)
                },
                new WorkspaceQuotaSnapshot
                {
                    Id = Guid.Parse("22220000-0000-0000-0000-000000000002"),
                    WorkspaceId = Guid.Parse("ffffffff-2222-2222-2222-222222222222"),
                    CurrentBalance = 950m,
                    ReservedCredits = 100m,
                    LowCreditThreshold = 100m,
                    CurrentMode = QuotaMode.FullVoice,
                    UpdatedAt = SeedCreatedAt
                },
                new WorkspaceQuotaSnapshot
                {
                    Id = Guid.Parse("22220000-0000-0000-0000-000000000003"),
                    WorkspaceId = Guid.Parse("ffffffff-3333-3333-3333-333333333333"),
                    CurrentBalance = 30m,
                    ReservedCredits = 5m,
                    LowCreditThreshold = 10m,
                    CurrentMode = QuotaMode.FullVoice,
                    UpdatedAt = SeedCreatedAt
                }
            );
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

            e.HasData(
                new UsageEvent
                {
                    Id = Guid.Parse("33330000-0000-0000-0000-000000000001"),
                    WorkspaceId = Guid.Parse("ffffffff-1111-1111-1111-111111111111"),
                    MeetingId = Guid.Parse("00000000-1111-1111-1111-111111111111"),
                    MeetingUsageSessionId = Guid.Parse("44440000-0000-0000-0000-000000000001"),
                    SpeakerUserId = Guid.Parse("dddddddd-1111-1111-1111-111111111111"),
                    FeatureType = BillingFeatureType.VoiceTranslation,
                    SourceLanguage = "en",
                    TargetLanguage = "vi",
                    DurationMs = 1800000,
                    TargetLanguageCount = 1,
                    IsVoiceCloningEnabled = false,
                    CalculatedCredits = 50m,
                    Status = UsageEventStatus.Processed,
                    IdempotencyKey = "usage-event-001",
                    CreatedAt = new DateTime(2026, 1, 2, 10, 30, 0, DateTimeKind.Utc)
                },
                new UsageEvent
                {
                    Id = Guid.Parse("33330000-0000-0000-0000-000000000002"),
                    WorkspaceId = Guid.Parse("ffffffff-1111-1111-1111-111111111111"),
                    MeetingId = Guid.Parse("00000000-1111-1111-1111-111111111111"),
                    MeetingUsageSessionId = Guid.Parse("44440000-0000-0000-0000-000000000001"),
                    SpeakerUserId = Guid.Parse("dddddddd-3333-3333-3333-333333333333"),
                    FeatureType = BillingFeatureType.VoiceTranslation,
                    SourceLanguage = "vi",
                    TargetLanguage = "en",
                    DurationMs = 1800000,
                    TargetLanguageCount = 1,
                    IsVoiceCloningEnabled = false,
                    CalculatedCredits = 25.5m,
                    Status = UsageEventStatus.Processed,
                    IdempotencyKey = "usage-event-002",
                    CreatedAt = new DateTime(2026, 1, 2, 10, 30, 0, DateTimeKind.Utc)
                },
                new UsageEvent
                {
                    Id = Guid.Parse("33330000-0000-0000-0000-000000000003"),
                    WorkspaceId = Guid.Parse("ffffffff-1111-1111-1111-111111111111"),
                    MeetingId = Guid.Parse("00000000-2222-2222-2222-222222222222"),
                    MeetingUsageSessionId = Guid.Parse("44440000-0000-0000-0000-000000000002"),
                    SpeakerUserId = Guid.Parse("eeeeeeee-2222-2222-2222-222222222222"),
                    FeatureType = BillingFeatureType.VoiceCloning,
                    SourceLanguage = "en",
                    TargetLanguage = "en",
                    DurationMs = 900000,
                    TargetLanguageCount = 2,
                    IsVoiceCloningEnabled = true,
                    CalculatedCredits = 50m,
                    Status = UsageEventStatus.Processed,
                    IdempotencyKey = "usage-event-voice-clone-001",
                    CreatedAt = new DateTime(2026, 1, 3, 14, 15, 0, DateTimeKind.Utc)
                },
                new UsageEvent
                {
                    Id = Guid.Parse("33330000-0000-0000-0000-000000000004"),
                    WorkspaceId = Guid.Parse("ffffffff-2222-2222-2222-222222222222"),
                    MeetingId = Guid.Parse("00000000-3333-3333-3333-333333333333"),
                    MeetingUsageSessionId = Guid.Parse("44440000-0000-0000-0000-000000000003"),
                    SpeakerUserId = Guid.Parse("eeeeeeee-1111-1111-1111-111111111111"),
                    FeatureType = BillingFeatureType.VoiceTranslation,
                    SourceLanguage = "en",
                    TargetLanguage = "vi",
                    DurationMs = 3600000,
                    TargetLanguageCount = 3,
                    IsVoiceCloningEnabled = false,
                    CalculatedCredits = 120m,
                    Status = UsageEventStatus.Processed,
                    IdempotencyKey = "usage-event-premium-001",
                    CreatedAt = new DateTime(2026, 1, 5, 11, 0, 0, DateTimeKind.Utc)
                }
            );
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

            e.HasData(
                new MeetingUsageSession
                {
                    Id = Guid.Parse("44440000-0000-0000-0000-000000000001"),
                    WorkspaceId = Guid.Parse("ffffffff-1111-1111-1111-111111111111"),
                    MeetingId = Guid.Parse("00000000-1111-1111-1111-111111111111"),
                    HostUserId = Guid.Parse("dddddddd-1111-1111-1111-111111111111"),
                    IsVoiceMode = true,
                    ActiveParticipants = 3,
                    ActiveLanguageStreams = 2,
                    EstimatedCreditsConsumed = 75.5m,
                    QuotaMode = QuotaMode.FullVoice,
                    Status = UsageSessionStatus.Ended,
                    StartedAt = new DateTime(2026, 1, 2, 10, 0, 0, DateTimeKind.Utc),
                    EndedAt = new DateTime(2026, 1, 2, 11, 0, 0, DateTimeKind.Utc)
                },
                new MeetingUsageSession
                {
                    Id = Guid.Parse("44440000-0000-0000-0000-000000000002"),
                    WorkspaceId = Guid.Parse("ffffffff-1111-1111-1111-111111111111"),
                    MeetingId = Guid.Parse("00000000-2222-2222-2222-222222222222"),
                    HostUserId = Guid.Parse("eeeeeeee-2222-2222-2222-222222222222"),
                    IsVoiceMode = true,
                    ActiveParticipants = 2,
                    ActiveLanguageStreams = 2,
                    EstimatedCreditsConsumed = 50m,
                    QuotaMode = QuotaMode.FullVoice,
                    Status = UsageSessionStatus.Ended,
                    StartedAt = new DateTime(2026, 1, 3, 13, 30, 0, DateTimeKind.Utc),
                    EndedAt = new DateTime(2026, 1, 3, 14, 45, 0, DateTimeKind.Utc)
                },
                new MeetingUsageSession
                {
                    Id = Guid.Parse("44440000-0000-0000-0000-000000000003"),
                    WorkspaceId = Guid.Parse("ffffffff-2222-2222-2222-222222222222"),
                    MeetingId = Guid.Parse("00000000-3333-3333-3333-333333333333"),
                    HostUserId = Guid.Parse("eeeeeeee-1111-1111-1111-111111111111"),
                    IsVoiceMode = true,
                    ActiveParticipants = 5,
                    ActiveLanguageStreams = 3,
                    EstimatedCreditsConsumed = 180m,
                    QuotaMode = QuotaMode.FullVoice,
                    Status = UsageSessionStatus.Active,
                    StartedAt = new DateTime(2026, 1, 5, 10, 30, 0, DateTimeKind.Utc),
                    EndedAt = null
                }
            );
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

            e.HasData(
                new Transaction
                {
                    Id = Guid.Parse("55550000-0000-0000-0000-000000000001"),
                    WorkspaceId = Guid.Parse("ffffffff-1111-1111-1111-111111111111"),
                    OwnerUserId = Guid.Parse("dddddddd-1111-1111-1111-111111111111"),
                    SubscriptionId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                    PlanId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                    Type = TransactionType.SubscriptionPurchase,
                    OrderCode = 10001,
                    AmountVnd = 199000,
                    Status = TransactionStatus.Success,
                    PaymentProvider = "PayOS",
                    PayOsTransactionId = "payos_txn_001",
                    IdempotencyKey = "txn-sub-pro-001",
                    FailureReason = null,
                    CreatedAt = SeedCreatedAt,
                    CompletedAt = SeedCreatedAt,
                    PurchasedCredits = 0
                },
                new Transaction
                {
                    Id = Guid.Parse("55550000-0000-0000-0000-000000000002"),
                    WorkspaceId = Guid.Parse("ffffffff-1111-1111-1111-111111111111"),
                    OwnerUserId = Guid.Parse("dddddddd-1111-1111-1111-111111111111"),
                    SubscriptionId = null,
                    PlanId = null,
                    Type = TransactionType.CreditTopUp,
                    OrderCode = 10002,
                    AmountVnd = 99000,
                    Status = TransactionStatus.Success,
                    PaymentProvider = "PayOS",
                    PayOsTransactionId = "payos_txn_002",
                    IdempotencyKey = "txn-credit-topup-001",
                    FailureReason = null,
                    CreatedAt = new DateTime(2026, 1, 2, 8, 0, 0, DateTimeKind.Utc),
                    CompletedAt = new DateTime(2026, 1, 2, 8, 5, 0, DateTimeKind.Utc),
                    PurchasedCredits = 200
                },
                new Transaction
                {
                    Id = Guid.Parse("55550000-0000-0000-0000-000000000003"),
                    WorkspaceId = Guid.Parse("ffffffff-2222-2222-2222-222222222222"),
                    OwnerUserId = Guid.Parse("eeeeeeee-1111-1111-1111-111111111111"),
                    SubscriptionId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                    PlanId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                    Type = TransactionType.SubscriptionPurchase,
                    OrderCode = 10003,
                    AmountVnd = 499000,
                    Status = TransactionStatus.Success,
                    PaymentProvider = "PayOS",
                    PayOsTransactionId = "payos_txn_003",
                    IdempotencyKey = "txn-sub-premium-001",
                    FailureReason = null,
                    CreatedAt = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
                    CompletedAt = new DateTime(2026, 1, 1, 12, 10, 0, DateTimeKind.Utc),
                    PurchasedCredits = 0
                },
                new Transaction
                {
                    Id = Guid.Parse("55550000-0000-0000-0000-000000000004"),
                    WorkspaceId = Guid.Parse("ffffffff-1111-1111-1111-111111111111"),
                    OwnerUserId = Guid.Parse("dddddddd-1111-1111-1111-111111111111"),
                    SubscriptionId = null,
                    PlanId = null,
                    Type = TransactionType.Refund,
                    OrderCode = 10004,
                    AmountVnd = -99000,
                    Status = TransactionStatus.Success,
                    PaymentProvider = "PayOS",
                    PayOsTransactionId = "payos_txn_004",
                    IdempotencyKey = "txn-refund-failed-topup-001",
                    FailureReason = null,
                    CreatedAt = new DateTime(2026, 1, 3, 10, 0, 0, DateTimeKind.Utc),
                    CompletedAt = new DateTime(2026, 1, 3, 10, 15, 0, DateTimeKind.Utc),
                    PurchasedCredits = 0
                },
                new Transaction
                {
                    Id = Guid.Parse("55550000-0000-0000-0000-000000000005"),
                    WorkspaceId = Guid.Parse("ffffffff-3333-3333-3333-333333333333"),
                    OwnerUserId = Guid.Parse("dddddddd-2222-2222-2222-222222222222"),
                    SubscriptionId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
                    PlanId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    Type = TransactionType.SubscriptionPurchase,
                    OrderCode = 10005,
                    AmountVnd = 0,
                    Status = TransactionStatus.Success,
                    PaymentProvider = "PayOS",
                    PayOsTransactionId = null,
                    IdempotencyKey = "txn-sub-free-001",
                    FailureReason = null,
                    CreatedAt = SeedCreatedAt,
                    CompletedAt = SeedCreatedAt,
                    PurchasedCredits = 0
                }
            );
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

            e.HasData(
                new QuotaAuditLog
                {
                    Id = Guid.Parse("66660000-0000-0000-0000-000000000001"),
                    WorkspaceId = Guid.Parse("ffffffff-1111-1111-1111-111111111111"),
                    UserId = Guid.Parse("dddddddd-1111-1111-1111-111111111111"),
                    MeetingId = null,
                    RelatedLedgerEntryId = Guid.Parse("11110000-0000-0000-0000-000000000001"),
                    Action = AuditAction.Allocate,
                    Description = "Initial credits allocated for Pro plan subscription",
                    MetadataJson = "{\"plan\": \"Pro\", \"initial_credits\": 500}",
                    CreatedAt = SeedCreatedAt
                },
                new QuotaAuditLog
                {
                    Id = Guid.Parse("66660000-0000-0000-0000-000000000002"),
                    WorkspaceId = Guid.Parse("ffffffff-1111-1111-1111-111111111111"),
                    UserId = Guid.Parse("dddddddd-1111-1111-1111-111111111111"),
                    MeetingId = Guid.Parse("00000000-1111-1111-1111-111111111111"),
                    RelatedLedgerEntryId = Guid.Parse("11110000-0000-0000-0000-000000000002"),
                    Action = AuditAction.Deduct,
                    Description = "Credits deducted for meeting translation usage",
                    MetadataJson = "{\"meeting_id\": \"00000000-1111-1111-1111-111111111111\", \"credits_deducted\": 75.5}",
                    CreatedAt = new DateTime(2026, 1, 2, 10, 30, 0, DateTimeKind.Utc)
                },
                new QuotaAuditLog
                {
                    Id = Guid.Parse("66660000-0000-0000-0000-000000000003"),
                    WorkspaceId = Guid.Parse("ffffffff-1111-1111-1111-111111111111"),
                    UserId = Guid.Parse("dddddddd-1111-1111-1111-111111111111"),
                    MeetingId = null,
                    RelatedLedgerEntryId = Guid.Parse("11110000-0000-0000-0000-000000000004"),
                    Action = AuditAction.Refund,
                    Description = "Refund issued for failed meeting transaction",
                    MetadataJson = "{\"reason\": \"Failed transaction\", \"refund_amount\": 10}",
                    CreatedAt = new DateTime(2026, 1, 4, 9, 0, 0, DateTimeKind.Utc)
                },
                new QuotaAuditLog
                {
                    Id = Guid.Parse("66660000-0000-0000-0000-000000000004"),
                    WorkspaceId = Guid.Parse("ffffffff-1111-1111-1111-111111111111"),
                    UserId = null,
                    MeetingId = Guid.Parse("00000000-2222-2222-2222-222222222222"),
                    RelatedLedgerEntryId = Guid.Parse("11110000-0000-0000-0000-000000000003"),
                    Action = AuditAction.MeetingStarted,
                    Description = "Meeting started with voice cloning enabled",
                    MetadataJson = "{\"host_user_id\": \"eeeeeeee-2222-2222-2222-222222222222\", \"voice_cloning_enabled\": true}",
                    CreatedAt = new DateTime(2026, 1, 3, 14, 15, 0, DateTimeKind.Utc)
                },
                new QuotaAuditLog
                {
                    Id = Guid.Parse("66660000-0000-0000-0000-000000000005"),
                    WorkspaceId = Guid.Parse("ffffffff-2222-2222-2222-222222222222"),
                    UserId = Guid.Parse("eeeeeeee-1111-1111-1111-111111111111"),
                    MeetingId = null,
                    RelatedLedgerEntryId = Guid.Parse("11110000-0000-0000-0000-000000000005"),
                    Action = AuditAction.Allocate,
                    Description = "Initial credits allocated for Premium plan subscription",
                    MetadataJson = "{\"plan\": \"Premium\", \"initial_credits\": 1000}",
                    CreatedAt = SeedCreatedAt
                },
                new QuotaAuditLog
                {
                    Id = Guid.Parse("66660000-0000-0000-0000-000000000006"),
                    WorkspaceId = Guid.Parse("ffffffff-2222-2222-2222-222222222222"),
                    UserId = null,
                    MeetingId = Guid.Parse("00000000-3333-3333-3333-333333333333"),
                    RelatedLedgerEntryId = null,
                    Action = AuditAction.MeetingStarted,
                    Description = "Meeting started with 5 participants and 3 language streams",
                    MetadataJson = "{\"participants\": 5, \"language_streams\": 3}",
                    CreatedAt = new DateTime(2026, 1, 5, 10, 30, 0, DateTimeKind.Utc)
                }
            );
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