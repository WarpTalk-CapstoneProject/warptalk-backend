using Microsoft.EntityFrameworkCore;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Infrastructure.Persistence;

public partial class BillingDbContext
{
    // Not part of the scaffolded DbSet list on this context (unlike the older,
    // now-unused Persistence.BillingDbContext) — added here so IUnitOfWork's
    // idempotency lookups have a mapped entity to query against.
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    // Rate card and admin-editable config tables. These were previously reached
    // only through hand-written ADO.NET commands on the raw connection; mapping
    // them here lets their repositories use EF Core like every other billing
    // repository, and keeps their transactions inside the same DbContext.
    public DbSet<UsageRateCard> UsageRateCards => Set<UsageRateCard>();
    public DbSet<BillingPricingConfig> BillingPricingConfigs => Set<BillingPricingConfig>();
    public DbSet<BillingPolicyConfig> BillingPolicyConfigs => Set<BillingPolicyConfig>();

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UsageRateCard>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("usage_rate_card_pkey");
            entity.ToTable("usage_rate_card", "subscription");

            entity.Property(e => e.Id).HasColumnName("id").HasDefaultValueSql("uuidv7()");
            entity.Property(e => e.ChargeType).HasColumnName("charge_type").HasMaxLength(30).IsRequired();
            entity.Property(e => e.Unit).HasColumnName("unit").HasMaxLength(30);
            entity.Property(e => e.Provider).HasColumnName("provider").HasMaxLength(50);
            entity.Property(e => e.Model).HasColumnName("model").HasMaxLength(100);
            entity.Property(e => e.SourceLanguageCode).HasColumnName("source_language_code").HasMaxLength(15);
            entity.Property(e => e.TargetLanguageCode).HasColumnName("target_language_code").HasMaxLength(15);
            // Widened from numeric(12,6) to numeric(18,6) by migration 005.
            entity.Property(e => e.UnitPrice).HasColumnName("unit_price").HasPrecision(18, 6);
            entity.Property(e => e.Currency).HasColumnName("currency").HasMaxLength(3).IsRequired();
            entity.Property(e => e.ProviderUnitCost).HasColumnName("provider_unit_cost").HasPrecision(18, 10);
            entity.Property(e => e.MarkupMultiplier).HasColumnName("markup_multiplier").HasPrecision(10, 4);
            entity.Property(e => e.EffectiveFrom).HasColumnName("effective_from");
            entity.Property(e => e.EffectiveTo).HasColumnName("effective_to");
            // Deliberately no HasDefaultValue(true): the column defaults to true in
            // the database, but declaring that here would make EF treat an explicit
            // false as "unset" and omit it from the INSERT, so deactivating a rate
            // card would silently insert it as active instead.
            entity.Property(e => e.IsActive).HasColumnName("is_active");
            entity.Property(e => e.Notes).HasColumnName("notes");
        });

        modelBuilder.Entity<BillingPricingConfig>(entity =>
        {
            entity.HasKey(e => e.Key).HasName("billing_pricing_config_pkey");
            entity.ToTable("billing_pricing_config", "subscription");

            entity.Property(e => e.Key).HasColumnName("key").HasMaxLength(80);
            entity.Property(e => e.Value).HasColumnName("value").HasPrecision(18, 6);
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<BillingPolicyConfig>(entity =>
        {
            entity.HasKey(e => e.Key).HasName("billing_policy_config_pkey");
            entity.ToTable("billing_policy_config", "subscription");

            entity.Property(e => e.Key).HasColumnName("key").HasMaxLength(100);
            entity.Property(e => e.Value).HasColumnName("value").HasPrecision(18, 6);
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("outbox_messages_pkey");
            entity.ToTable("outbox_messages", "subscription");
            entity.HasIndex(e => new { e.PublishedAt, e.AvailableAt, e.CreatedAt })
                .HasDatabaseName("idx_outbox_messages_dispatch");
            entity.Property(e => e.Id).HasColumnName("id").HasDefaultValueSql("uuidv7()");
            entity.Property(e => e.EventType).HasColumnName("event_type").HasMaxLength(150).IsRequired();
            entity.Property(e => e.SchemaVersion).HasColumnName("schema_version");
            entity.Property(e => e.OccurredAt).HasColumnName("occurred_at");
            entity.Property(e => e.Producer).HasColumnName("producer").HasMaxLength(100).IsRequired();
            entity.Property(e => e.CorrelationId).HasColumnName("correlation_id").HasMaxLength(100);
            entity.Property(e => e.CausationId).HasColumnName("causation_id").HasMaxLength(100);
            entity.Property(e => e.WorkspaceId).HasColumnName("workspace_id");
            entity.Property(e => e.PayloadJson).HasColumnName("payload_json").HasColumnType("jsonb").IsRequired();
            entity.Property(e => e.AttemptCount).HasColumnName("attempt_count");
            entity.Property(e => e.AvailableAt).HasColumnName("available_at");
            entity.Property(e => e.PublishedAt).HasColumnName("published_at");
            entity.Property(e => e.LockedAt).HasColumnName("locked_at");
            entity.Property(e => e.DeadLetteredAt).HasColumnName("dead_lettered_at");
            entity.Property(e => e.LastError).HasColumnName("last_error");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");
        });

        modelBuilder.Entity<InboxMessage>(entity =>
        {
            entity.HasKey(e => new { e.EventId, e.Consumer }).HasName("inbox_messages_pkey");
            entity.ToTable("inbox_messages", "subscription");
            entity.Property(e => e.EventId).HasColumnName("event_id");
            entity.Property(e => e.Consumer).HasColumnName("consumer").HasMaxLength(150);
            entity.Property(e => e.ProcessedAt).HasColumnName("processed_at");
            entity.Property(e => e.EventType).HasColumnName("event_type").HasMaxLength(150);
            entity.Property(e => e.LastError).HasColumnName("last_error");
        });

        modelBuilder.Entity<Plan>()
            .HasQueryFilter(p => p.DeletedAt == null);

        modelBuilder.Entity<Subscription>()
            .HasQueryFilter(s => s.DeletedAt == null);

        // Keep required dependents aligned with the Subscription soft-delete filter.
        // Without matching filters EF warns that required navigations can be
        // silently removed from query results when their subscription is deleted.
        modelBuilder.Entity<CreditTransaction>()
            .HasQueryFilter(t => t.Subscription!.DeletedAt == null);
        modelBuilder.Entity<CreditBalanceSnapshot>()
            .HasQueryFilter(s => s.Subscription.DeletedAt == null);
        modelBuilder.Entity<UsageRecord>()
            .HasQueryFilter(u => u.Subscription.DeletedAt == null);
        modelBuilder.Entity<Payment>()
            .HasQueryFilter(p => p.Subscription.DeletedAt == null);
        modelBuilder.Entity<Transaction>()
            .HasQueryFilter(t => t.Subscription!.DeletedAt == null);
        modelBuilder.Entity<Invoice>()
            .HasQueryFilter(i => i.Payment.Subscription.DeletedAt == null);


        modelBuilder.Entity<Subscription>(entity =>
        {
            entity.Property(e => e.Status)
                .HasColumnName("status")
                .HasDefaultValue("active");
        });

        modelBuilder.Entity<Payment>(entity =>
        {
            entity.Property(e => e.Status)
                .HasColumnName("status")
                .HasDefaultValue("pending");
        });

        modelBuilder.Entity<CreditTransaction>(entity =>
        {
            entity.Property(e => e.Type)
                .HasColumnName("type");
            entity.Property(e => e.CorrelationId)
                .HasColumnName("correlation_id")
                .HasMaxLength(100);
            entity.Property(e => e.Status)
                .HasColumnName("status")
                .HasMaxLength(20)
                .HasDefaultValue("committed");
            entity.HasIndex(e => new { e.CorrelationId, e.Type })
                .IsUnique()
                .HasDatabaseName("ix_credit_transactions_correlation_type");
        });

    }
}
