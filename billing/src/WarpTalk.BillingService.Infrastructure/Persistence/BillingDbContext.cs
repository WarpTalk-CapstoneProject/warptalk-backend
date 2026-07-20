using System;
using Microsoft.EntityFrameworkCore;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Enums;

namespace WarpTalk.BillingService.Infrastructure.Persistence;

public partial class BillingDbContext : DbContext
{
    public BillingDbContext(DbContextOptions<BillingDbContext> options)
        : base(options)
    {
    }

    public virtual DbSet<Plan> Plans { get; set; }

    public virtual DbSet<Subscription> Subscriptions { get; set; }

    public virtual DbSet<Transaction> Transactions { get; set; }

    public virtual DbSet<CreditTransaction> CreditTransactions { get; set; }

    public virtual DbSet<IdempotencyRecord> IdempotencyRecords { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("pgcrypto");

        // NOTE: schema is "subscription", not "billing" — "billing" has never existed in
        // this database (init-db.sql only creates schema subscription). See migration
        // 019-16-07-2026-billing-schema-mismatch-and-idempotency.sql for the full story.
        modelBuilder.Entity<Plan>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("plans_pkey");

            entity.ToTable("plans", "subscription");

            entity.HasIndex(e => e.Slug, "plans_slug_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuidv7()")
                .HasColumnName("id");
            entity.Property(e => e.Name).HasMaxLength(100).HasColumnName("name");
            entity.Property(e => e.Slug).HasMaxLength(50).HasColumnName("slug");
            entity.Property(e => e.Tier).HasMaxLength(20).HasColumnName("tier");
            entity.Property(e => e.Price).HasPrecision(12, 2).HasColumnName("price");
            entity.Property(e => e.Currency).HasMaxLength(3).HasColumnName("currency");
            entity.Property(e => e.BillingCycle).HasMaxLength(20).HasColumnName("billing_cycle");
            entity.Property(e => e.CreditsPerMonth).HasColumnName("credits_per_cycle");
            entity.Property(e => e.MaxParticipants).HasColumnName("max_participants");
            entity.Property(e => e.MaxLanguages).HasColumnName("max_languages");
            entity.Property(e => e.VoiceCloneEnabled).HasColumnName("voice_clone_enabled");
            entity.Property(e => e.AiAssistantEnabled).HasColumnName("ai_assistant_enabled");
            entity.Property(e => e.GlossaryEnabled).HasColumnName("glossary_enabled");
            entity.Property(e => e.DedicatedGpu).HasColumnName("dedicated_gpu");
            entity.Property(e => e.Features).HasColumnType("jsonb").HasColumnName("features");
            entity.Property(e => e.SortOrder).HasColumnName("sort_order");
            entity.Property(e => e.IsActive).HasColumnName("is_active");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()").HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()").HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
        });

        modelBuilder.Entity<Subscription>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("subscriptions_pkey");

            entity.ToTable("subscriptions", "subscription");

            entity.HasIndex(e => e.PlanId, "idx_subscriptions_plan");

            entity.HasIndex(e => e.WorkspaceId, "idx_subscriptions_workspace");

            // Mirrors subscriptions_one_active_per_workspace_idx (migration 017 Step 0),
            // which is the real DB-level constraint — keyed on is_active, not status.
            entity.HasIndex(e => e.WorkspaceId)
                .HasDatabaseName("subscriptions_one_active_per_workspace_idx")
                .IsUnique()
                .HasFilter("is_active = true");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuidv7()")
                .HasColumnName("id");
            entity.Property(e => e.UserId).HasColumnName("user_id");
            entity.Property(e => e.CurrentCredits)
                .HasDefaultValue(0)
                .HasColumnName("credits_remaining");
            entity.Property(e => e.CreditsUsedThisCycle)
                .HasDefaultValue(0)
                .HasColumnName("credits_used_this_cycle");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.EndDate).HasColumnName("current_period_end");
            entity.Property(e => e.PlanId).HasColumnName("plan_id");

            entity.Property(e => e.StartDate)
                .IsRequired()
                .HasColumnName("current_period_start");
            entity.Property(e => e.AutoRenew).HasDefaultValue(true).HasColumnName("auto_renew");
            entity.Property(e => e.CancellationReason).HasColumnName("cancellation_reason");
            entity.Property(e => e.CancelledAt).HasColumnName("cancelled_at");
            entity.Property(e => e.TrialEndsAt).HasColumnName("trial_ends_at");
            entity.Property(e => e.IsActive).HasDefaultValue(true).HasColumnName("is_active");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()").HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.Status)
                .IsRequired()
                .HasMaxLength(20)
                .HasConversion<string>()
                .HasColumnName("status");
            entity.Property(e => e.WorkspaceId).HasColumnName("workspace_id");

            entity.HasOne(d => d.Plan).WithMany(p => p.Subscriptions)
                .HasForeignKey(d => d.PlanId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("subscriptions_plan_id_fkey");
        });

        modelBuilder.Entity<Transaction>(entity =>
        {
            // Maps to subscription.payments — no "transactions" table in the real schema.
            entity.HasKey(e => e.Id).HasName("payments_pkey");

            entity.ToTable("payments", "subscription");

            entity.HasIndex(e => e.SubscriptionId, "idx_payments_subscription");

            entity.HasIndex(e => e.ProviderTransactionId, "payments_provider_transaction_id_key")
                .IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuidv7()")
                .HasColumnName("id");
            entity.Property(e => e.SubscriptionId).IsRequired().HasColumnName("subscription_id");
            entity.Property(e => e.UserId).IsRequired().HasColumnName("user_id");
            entity.Property(e => e.Amount).IsRequired().HasPrecision(12, 2).HasColumnName("amount");
            entity.Property(e => e.TaxAmount).HasDefaultValue(0m).HasPrecision(12, 2).HasColumnName("tax_amount");
            entity.Property(e => e.TotalAmount).IsRequired().HasPrecision(12, 2).HasColumnName("total_amount");
            entity.Property(e => e.Currency).HasMaxLength(3).HasColumnName("currency");
            entity.Property(e => e.PaymentMethod).IsRequired().HasMaxLength(30).HasColumnName("payment_method");
            entity.Property(e => e.Provider).HasMaxLength(30).HasColumnName("provider");
            entity.Property(e => e.ProviderTransactionId).HasMaxLength(255).HasColumnName("provider_transaction_id");
            entity.Property(e => e.ProviderOrderId).HasMaxLength(255).HasColumnName("provider_order_id");
            entity.Property(e => e.Status)
                .IsRequired()
                .HasMaxLength(20)
                .HasConversion<string>()
                .HasColumnName("status");
            entity.Property(e => e.FailureReason).HasMaxLength(500).HasColumnName("failure_reason");
            entity.Property(e => e.ProviderMetadata).HasColumnType("jsonb").HasColumnName("provider_metadata");
            entity.Property(e => e.PaidAt).HasColumnName("paid_at");
            entity.Property(e => e.RefundedAt).HasColumnName("refunded_at");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()").HasColumnName("created_at");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()").HasColumnName("updated_at");
            entity.Ignore(e => e.ExternalId);

            entity.HasOne(d => d.Subscription).WithMany(p => p.Transactions)
                .HasForeignKey(d => d.SubscriptionId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("payments_subscription_id_fkey");
        });

        modelBuilder.Entity<CreditTransaction>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("credit_transactions_pkey");

            entity.ToTable("credit_transactions", "subscription");

            entity.HasIndex(e => e.SubscriptionId, "idx_credit_transactions_subscription");

            entity.HasIndex(e => new { e.SubscriptionId, e.CreatedAt })
                .HasDatabaseName("idx_credit_transactions_workspace_created");

            entity.HasIndex(e => e.ReferenceId, "idx_credit_transactions_reference");

            entity.HasIndex(e => e.IdempotencyKey)
                .HasDatabaseName("credit_transactions_idempotency_key_idx")
                .IsUnique()
                .HasFilter("idempotency_key IS NOT NULL");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuidv7()")
                .HasColumnName("id");
            entity.Property(e => e.SubscriptionId).IsRequired().HasColumnName("subscription_id");
            entity.Property(e => e.UserId).IsRequired().HasColumnName("user_id");
            entity.Property(e => e.Amount).HasColumnName("amount");
            entity.Property(e => e.Description).HasMaxLength(255).HasColumnName("description");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.ReferenceId)
                .HasColumnName("reference_id");
            entity.Property(e => e.ReferenceType)
                .HasMaxLength(30)
                .HasColumnName("reference_type");
            entity.Property(e => e.Type)
                .HasMaxLength(20)
                .HasConversion<string>()
                .HasColumnName("type");
            entity.Property(e => e.BalanceAfter).IsRequired().HasColumnName("balance_after");
            entity.Property(e => e.ChargeType).HasMaxLength(30).HasColumnName("charge_type");
            entity.Property(e => e.PricingRateCardId).HasColumnName("pricing_rate_card_id");
            entity.Property(e => e.UsageRecordId).HasColumnName("usage_record_id");
            entity.Property(e => e.UnitPriceSnapshot).HasPrecision(12, 6).HasColumnName("unit_price_snapshot");
            entity.Property(e => e.InvoiceId).HasColumnName("invoice_id");
            entity.Property(e => e.ReversalOfTransactionId).HasColumnName("reversal_of_transaction_id");
            entity.Property(e => e.Currency).HasMaxLength(3).HasColumnName("currency");
            entity.Property(e => e.IdempotencyKey).HasMaxLength(255).HasColumnName("idempotency_key");
            entity.Property(e => e.TriggeredByParticipantId).HasColumnName("triggered_by_participant_id");
            entity.Property(e => e.TranscriptSegmentId).HasColumnName("transcript_segment_id");
            entity.Ignore(e => e.WorkspaceId);

            entity.HasOne(d => d.Subscription)
                .WithMany()
                .HasForeignKey(d => d.SubscriptionId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("credit_transactions_subscription_id_fkey");
        });

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

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
