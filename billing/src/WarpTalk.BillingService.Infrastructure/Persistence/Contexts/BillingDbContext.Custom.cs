using Microsoft.EntityFrameworkCore;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Enums;

namespace WarpTalk.BillingService.Infrastructure.Persistence.Contexts;

public partial class BillingDbContext
{
    // Not part of the scaffolded DbSet list on this context (unlike the older,
    // now-unused Persistence.BillingDbContext) — added here so IUnitOfWork's
    // idempotency lookups have a mapped entity to query against.
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<IdempotencyRecord>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("idempotency_records_pkey");

            entity.ToTable("idempotency_records", "subscription");

            entity.HasIndex(e => new { e.Key, e.Operation }, "ux_idempotency_records_key_operation").IsUnique();
            entity.HasIndex(e => e.WorkspaceId, "idx_idempotency_records_workspace");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuidv7()")
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.ExpiresAt)
                .HasColumnName("expires_at");
            entity.Property(e => e.Key)
                .HasMaxLength(255)
                .HasColumnName("idempotency_key");
            entity.Property(e => e.Operation)
                .HasMaxLength(100)
                .HasColumnName("operation");
            entity.Property(e => e.RequestHash)
                .HasMaxLength(128)
                .HasColumnName("request_hash");
            entity.Property(e => e.ResponseJson)
                .HasColumnType("text")
                .HasColumnName("response_json");
            entity.Property(e => e.WorkspaceId).HasColumnName("workspace_id");
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
        modelBuilder.Entity<Refund>()
            .HasQueryFilter(r => r.Payment.Subscription.DeletedAt == null);

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
