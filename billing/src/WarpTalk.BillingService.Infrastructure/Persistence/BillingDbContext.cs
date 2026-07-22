using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Infrastructure.Persistence;

public partial class BillingDbContext : DbContext
{
    public BillingDbContext()
    {
    }

    public BillingDbContext(DbContextOptions<BillingDbContext> options)
        : base(options)
    {
    }

    public virtual DbSet<Plan> Plans { get; set; }

    public virtual DbSet<Subscription> Subscriptions { get; set; }

    public virtual DbSet<CreditTransaction> CreditTransactions { get; set; }

    public virtual DbSet<CreditBalanceSnapshot> CreditBalanceSnapshots { get; set; }

    public virtual DbSet<UsageRecord> UsageRecords { get; set; }

    public virtual DbSet<Payment> Payments { get; set; }

    public virtual DbSet<Invoice> Invoices { get; set; }

    public virtual DbSet<Refund> Refunds { get; set; }

    public virtual DbSet<SchemaMigration> SchemaMigrations { get; set; }

    public virtual DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Plan>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("plans_pkey");

            entity.ToTable("plans", "subscription", t => t.HasCheckConstraint("chk_billing_cycle", "billing_cycle IN ('monthly', 'semiannual', 'yearly')"));

            entity.HasIndex(e => e.Slug, "plans_slug_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuidv7()")
                .HasColumnName("id");
            entity.Property(e => e.Name)
                .HasMaxLength(100)
                .HasColumnName("name");
            entity.Property(e => e.Slug)
                .HasMaxLength(50)
                .HasColumnName("slug");
            entity.Property(e => e.Tier)
                .HasMaxLength(20)
                .HasColumnName("tier");
            entity.Property(e => e.Price)
                .HasPrecision(12, 2)
                .HasColumnName("price");
            entity.Property(e => e.Currency)
                .HasMaxLength(3)
                .HasDefaultValue("VND")
                .HasColumnName("currency");
            entity.Property(e => e.BillingCycle)
                .HasMaxLength(20)
                .HasDefaultValue("monthly")
                .HasColumnName("billing_cycle");
            entity.Property(e => e.CreditsPerCycle)
                .HasColumnName("credits_per_cycle");
            entity.Property(e => e.MaxParticipants)
                .HasDefaultValue(2)
                .HasColumnName("max_participants");
            entity.Property(e => e.MaxLanguages)
                .HasDefaultValue(2)
                .HasColumnName("max_languages");
            entity.Property(e => e.VoiceCloneEnabled)
                .HasDefaultValue(false)
                .HasColumnName("voice_clone_enabled");
            entity.Property(e => e.AiAssistantEnabled)
                .HasDefaultValue(false)
                .HasColumnName("ai_assistant_enabled");
            entity.Property(e => e.GlossaryEnabled)
                .HasDefaultValue(false)
                .HasColumnName("glossary_enabled");
            entity.Property(e => e.DedicatedGpu)
                .HasDefaultValue(false)
                .HasColumnName("dedicated_gpu");
            entity.Property(e => e.Features)
                .HasDefaultValueSql("'{}'::jsonb")
                .HasColumnType("jsonb")
                .HasColumnName("features");
            entity.Property(e => e.SortOrder)
                .HasDefaultValue(0)
                .HasColumnName("sort_order");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true)
                .HasColumnName("is_active");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy)
                .HasComment("External AuthService user id. No physical FK.")
                .HasColumnName("created_by");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy)
                .HasComment("External AuthService user id. No physical FK.")
                .HasColumnName("updated_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy)
                .HasComment("External AuthService user id. No physical FK.")
                .HasColumnName("deleted_by");
        });

        modelBuilder.Entity<Subscription>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("subscriptions_pkey");

            entity.ToTable("subscriptions", "subscription", t =>
            {
                t.HasCheckConstraint("chk_subscription_status", "status IN ('pending', 'active', 'cancelled', 'expired')");
                t.HasCheckConstraint("chk_subscription_credits", "credits_remaining >= -2147483648");
            });

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuidv7()")
                .HasColumnName("id");
            entity.Property(e => e.UserId)
                .HasComment("External AuthService user id. No physical FK.")
                .HasColumnName("user_id");
            entity.Property(e => e.WorkspaceId)
                .HasComment("External AuthService workspace id. No physical FK.")
                .HasColumnName("workspace_id");
            entity.Property(e => e.PlanId).HasColumnName("plan_id");
            entity.Property(e => e.Status)
                .HasMaxLength(20)
                .HasDefaultValue("active")
                .HasColumnName("status");
            entity.Property(e => e.CreditsRemaining)
                .HasDefaultValue(0)
                .HasColumnName("credits_remaining");
            entity.Property(e => e.CreditsUsedThisCycle)
                .HasDefaultValue(0)
                .HasColumnName("credits_used_this_cycle");
            entity.Property(e => e.CurrentPeriodStart).HasColumnName("current_period_start");
            entity.Property(e => e.CurrentPeriodEnd).HasColumnName("current_period_end");
            entity.Property(e => e.AutoRenew)
                .HasDefaultValue(true)
                .HasColumnName("auto_renew");
            entity.Property(e => e.CancellationReason).HasColumnName("cancellation_reason");
            entity.Property(e => e.CancelledAt).HasColumnName("cancelled_at");
            entity.Property(e => e.TrialEndsAt).HasColumnName("trial_ends_at");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true)
                .HasColumnName("is_active");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy)
                .HasComment("External AuthService user id. No physical FK.")
                .HasColumnName("created_by");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy)
                .HasComment("External AuthService user id. No physical FK.")
                .HasColumnName("updated_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy)
                .HasComment("External AuthService user id. No physical FK.")
                .HasColumnName("deleted_by");

            entity.Property(e => e.Version)
                .IsRowVersion()
                .HasColumnName("xmin")
                .HasColumnType("xid");

            entity.HasOne(d => d.Plan).WithMany(p => p.Subscriptions)
                .HasForeignKey(d => d.PlanId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("subscriptions_plan_id_fkey");
        });

        modelBuilder.Entity<CreditTransaction>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("credit_transactions_pkey");

            entity.ToTable("credit_transactions", "subscription");



            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuidv7()")
                .HasColumnName("id");
            entity.Property(e => e.SubscriptionId).HasColumnName("subscription_id");
            entity.Property(e => e.UserId)
                .HasComment("External AuthService user id. No physical FK.")
                .HasColumnName("user_id");
            entity.Property(e => e.Amount).HasColumnName("amount");
            entity.Property(e => e.Type)
                .HasMaxLength(20)
                .HasColumnName("type");
            entity.Property(e => e.Description)
                .HasMaxLength(255)
                .HasColumnName("description");
            entity.Property(e => e.ReferenceId).HasColumnName("reference_id");
            entity.Property(e => e.ReferenceType)
                .HasMaxLength(30)
                .HasColumnName("reference_type");
            entity.Property(e => e.BalanceAfter).HasColumnName("balance_after");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");

            entity.HasOne(d => d.Subscription).WithMany(p => p.CreditTransactions)
                .HasForeignKey(d => d.SubscriptionId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("credit_transactions_subscription_id_fkey");
        });

        modelBuilder.Entity<CreditBalanceSnapshot>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("credit_balance_snapshots_pkey");

            entity.ToTable("credit_balance_snapshots", "subscription");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuidv7()")
                .HasColumnName("id");
            entity.Property(e => e.SubscriptionId).HasColumnName("subscription_id");
            entity.Property(e => e.CreditsRemaining).HasColumnName("credits_remaining");
            entity.Property(e => e.CreditsUsedThisCycle).HasColumnName("credits_used_this_cycle");
            entity.Property(e => e.SnapshotAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("snapshot_at");

            entity.HasOne(d => d.Subscription).WithMany(p => p.CreditBalanceSnapshots)
                .HasForeignKey(d => d.SubscriptionId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("credit_balance_snapshots_subscription_id_fkey");
        });

        modelBuilder.Entity<UsageRecord>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("usage_records_pkey");

            entity.ToTable("usage_records", "subscription");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuidv7()")
                .HasColumnName("id");
            entity.Property(e => e.SubscriptionId).HasColumnName("subscription_id");
            entity.Property(e => e.UserId)
                .HasComment("External AuthService user id. No physical FK.")
                .HasColumnName("user_id");
            entity.Property(e => e.WorkspaceId)
                .HasComment("External AuthService workspace id. No physical FK.")
                .HasColumnName("workspace_id");
            entity.Property(e => e.TranslationRoomId)
                .HasComment("External TranslationRoomService room id. No physical FK.")
                .HasColumnName("translation_room_id");
            entity.Property(e => e.SegmentId)
                .HasComment("External Segment id. No physical FK.")
                .HasColumnName("segment_id");
            entity.Property(e => e.UsageType)
                .HasMaxLength(30)
                .HasColumnName("usage_type");
            entity.Property(e => e.Unit)
                .HasMaxLength(20)
                .HasDefaultValue("credit")
                .HasColumnName("unit");
            entity.Property(e => e.Quantity)
                .HasPrecision(12, 4)
                .HasDefaultValue(1m)
                .HasColumnName("quantity");
            entity.Property(e => e.CreditsConsumed).HasColumnName("credits_consumed");
            entity.Property(e => e.DurationSeconds).HasColumnName("duration_seconds");
            entity.Property(e => e.Details)
                .HasColumnType("jsonb")
                .HasColumnName("details");
            entity.Property(e => e.RecordedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("recorded_at");

            entity.HasOne(d => d.Subscription).WithMany(p => p.UsageRecords)
                .HasForeignKey(d => d.SubscriptionId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("usage_records_subscription_id_fkey");
        });

        modelBuilder.Entity<Payment>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("payments_pkey");

            entity.ToTable("payments", "subscription");

            entity.HasIndex(e => e.ProviderTransactionId, "payments_provider_transaction_id_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuidv7()")
                .HasColumnName("id");
            entity.Property(e => e.SubscriptionId).HasColumnName("subscription_id");
            entity.Property(e => e.UserId)
                .HasComment("External AuthService user id. No physical FK.")
                .HasColumnName("user_id");
            entity.Property(e => e.Amount)
                .HasPrecision(12, 2)
                .HasColumnName("amount");
            entity.Property(e => e.TaxAmount)
                .HasPrecision(12, 2)
                .HasDefaultValue(0m)
                .HasColumnName("tax_amount");
            entity.Property(e => e.TotalAmount)
                .HasPrecision(12, 2)
                .HasColumnName("total_amount");
            entity.Property(e => e.Currency)
                .HasMaxLength(3)
                .HasDefaultValue("VND")
                .HasColumnName("currency");
            entity.Property(e => e.PaymentMethod)
                .HasMaxLength(30)
                .HasColumnName("payment_method");
            entity.Property(e => e.Provider)
                .HasMaxLength(30)
                .HasDefaultValue("payos")
                .HasColumnName("provider");
            entity.Property(e => e.ProviderTransactionId)
                .HasMaxLength(255)
                .HasColumnName("provider_transaction_id");
            entity.Property(e => e.ProviderOrderId)
                .HasMaxLength(255)
                .HasColumnName("provider_order_id");
            entity.Property(e => e.Status)
                .HasMaxLength(20)
                .HasDefaultValue("pending")
                .HasColumnName("status");
            entity.Property(e => e.FailureReason)
                .HasMaxLength(500)
                .HasColumnName("failure_reason");
            entity.Property(e => e.ProviderMetadata)
                .HasColumnType("jsonb")
                .HasColumnName("provider_metadata");
            entity.Property(e => e.PaidAt).HasColumnName("paid_at");
            entity.Property(e => e.RefundedAt).HasColumnName("refunded_at");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.Subscription).WithMany(p => p.Payments)
                .HasForeignKey(d => d.SubscriptionId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("payments_subscription_id_fkey");
        });


        modelBuilder.Entity<SchemaMigration>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("schema_migrations_pkey");

            entity.ToTable("schema_migrations", "subscription");

            entity.HasIndex(e => e.MigrationKey, "schema_migrations_migration_key_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuidv7()")
                .HasColumnName("id");
            entity.Property(e => e.MigrationKey)
                .HasMaxLength(150)
                .HasColumnName("migration_key");
            entity.Property(e => e.MigrationName)
                .HasMaxLength(255)
                .HasColumnName("migration_name");
            entity.Property(e => e.Checksum)
                .HasMaxLength(128)
                .HasColumnName("checksum");
            entity.Property(e => e.ScriptPath)
                .HasMaxLength(500)
                .HasColumnName("script_path");
            entity.Property(e => e.Status)
                .HasMaxLength(20)
                .HasDefaultValue("success")
                .HasColumnName("status");
            entity.Property(e => e.StartedAt).HasColumnName("started_at");
            entity.Property(e => e.CompletedAt).HasColumnName("completed_at");
            entity.Property(e => e.ExecutionTimeMs).HasColumnName("execution_time_ms");
            entity.Property(e => e.ErrorMessage).HasColumnName("error_message");
            entity.Property(e => e.AppliedBy)
                .HasMaxLength(100)
                .HasColumnName("applied_by");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
        });

        modelBuilder.Entity<Invoice>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("invoices_pkey");
            entity.ToTable("invoices", "subscription");

            entity.HasIndex(e => e.InvoiceNumber, "invoices_invoice_number_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuidv7()")
                .HasColumnName("id");
            entity.Property(e => e.PaymentId).HasColumnName("payment_id");
            entity.Property(e => e.UserId).HasColumnName("user_id");
            entity.Property(e => e.InvoiceNumber)
                .HasMaxLength(30)
                .HasColumnName("invoice_number");
            entity.Property(e => e.Subtotal)
                .HasPrecision(12, 2)
                .HasColumnName("subtotal");
            entity.Property(e => e.Tax)
                .HasPrecision(12, 2)
                .HasDefaultValue(0m)
                .HasColumnName("tax");
            entity.Property(e => e.Total)
                .HasPrecision(12, 2)
                .HasColumnName("total");
            entity.Property(e => e.Currency)
                .HasMaxLength(3)
                .HasDefaultValue("VND")
                .HasColumnName("currency");
            entity.Property(e => e.Status)
                .HasMaxLength(20)
                .HasDefaultValue("issued")
                .HasColumnName("status");
            entity.Property(e => e.PdfUrl)
                .HasMaxLength(500)
                .HasColumnName("pdf_url");
            entity.Property(e => e.LineItems)
                .HasDefaultValueSql("'[]'::jsonb")
                .HasColumnType("jsonb")
                .HasColumnName("line_items");
            entity.Property(e => e.IssuedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("issued_at");
            entity.Property(e => e.DueAt).HasColumnName("due_at");
            entity.Property(e => e.PaidAt).HasColumnName("paid_at");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");

            entity.HasOne(d => d.Payment).WithMany()
                .HasForeignKey(d => d.PaymentId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("invoices_payment_id_fkey");
        });

        modelBuilder.Entity<Refund>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("refunds_pkey");
            entity.ToTable("refunds", "subscription");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuidv7()")
                .HasColumnName("id");
            entity.Property(e => e.PaymentId).HasColumnName("payment_id");
            entity.Property(e => e.UserId).HasColumnName("user_id");
            entity.Property(e => e.Amount)
                .HasPrecision(12, 2)
                .HasColumnName("amount");
            entity.Property(e => e.Reason)
                .HasMaxLength(500)
                .HasColumnName("reason");
            entity.Property(e => e.Status)
                .HasMaxLength(20)
                .HasDefaultValue("pending")
                .HasColumnName("status");
            entity.Property(e => e.ProviderRefundId)
                .HasMaxLength(255)
                .HasColumnName("provider_refund_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CompletedAt).HasColumnName("completed_at");

            entity.HasOne(d => d.Payment).WithMany(p => p.Refunds)
                .HasForeignKey(d => d.PaymentId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("refunds_payment_id_fkey");
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

        modelBuilder.Entity<Plan>()
            .HasQueryFilter(p => p.DeletedAt == null);

        modelBuilder.Entity<Subscription>()
            .HasQueryFilter(s => s.DeletedAt == null);

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
        });
    }
}

