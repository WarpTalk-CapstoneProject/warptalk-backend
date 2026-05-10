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

    public virtual DbSet<TokenTransaction> TokenTransactions { get; set; }

    public virtual DbSet<IdempotencyRecord> IdempotencyRecords { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("pgcrypto");

        modelBuilder.Entity<Plan>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("plans_pkey");

            entity.ToTable("plans", "billing");

            entity.HasIndex(e => e.Name, "plans_name_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuid_generate_v7()")
                .HasColumnName("id");
            entity.Property(e => e.TokensPerMonth)
                .HasColumnName("tokens_per_month");
            entity.Property(e => e.Name)
                .HasMaxLength(50)
                .HasColumnName("name");
            entity.Property(e => e.PricePerMonth)
                .HasPrecision(18, 2)
                .HasColumnName("price_per_month");
        });

        modelBuilder.Entity<Subscription>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("subscriptions_pkey");

            entity.ToTable("subscriptions", "billing", t => t.HasCheckConstraint(
                "chk_subscriptions_status",
                "status IN ('Pending', 'Active', 'Cancelled', 'Expired', 'Suspended')"));

            entity.HasIndex(e => e.PlanId, "idx_subscriptions_plan");

            entity.HasIndex(e => e.WorkspaceId, "idx_subscriptions_workspace");

            entity.HasIndex(e => e.WorkspaceId)
                .HasDatabaseName("ux_subscriptions_workspace_active")
                .IsUnique()
                .HasFilter("(lower(status) = 'active')");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuid_generate_v7()")
                .HasColumnName("id");
            entity.Property(e => e.CurrentTokens)
                .HasDefaultValue(0)
                .HasColumnName("current_tokens");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.EndDate).HasColumnName("end_date");
            entity.Property(e => e.PlanId).HasColumnName("plan_id");
            entity.Property(e => e.Duration)
                .HasMaxLength(10)
                .HasColumnName("duration");
            entity.Property(e => e.Tier)
                .HasMaxLength(20)
                .HasColumnName("tier");
            entity.Property(e => e.StartDate)
                .IsRequired()
                .HasColumnName("start_date");
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
            entity.HasKey(e => e.Id).HasName("transactions_pkey");

            entity.ToTable("transactions", "billing", t => t.HasCheckConstraint(
                "chk_transactions_status",
                "status IN ('Pending', 'Succeeded', 'Failed', 'Refunded', 'Cancelled')"));

            entity.HasIndex(e => e.WorkspaceId, "idx_transactions_workspace");

            entity.HasIndex(e => e.ExternalId, "transactions_external_id_key")
                .IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuid_generate_v7()")
                .HasColumnName("id");
            entity.Property(e => e.Amount)
                .IsRequired()
                .HasPrecision(18, 2)
                .HasColumnName("amount");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy)
                .HasMaxLength(255)
                .HasColumnName("created_by");
            entity.Property(e => e.ExternalId)
                .HasMaxLength(255)
                .HasColumnName("external_id");
            entity.Property(e => e.Status)
                .IsRequired()
                .HasMaxLength(20)
                .HasConversion<string>()
                .HasColumnName("status");
            entity.Property(e => e.SubscriptionId).HasColumnName("subscription_id");
            entity.Property(e => e.WorkspaceId).HasColumnName("workspace_id");

            entity.HasOne(d => d.Subscription).WithMany(p => p.Transactions)
                .HasForeignKey(d => d.SubscriptionId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("transactions_subscription_id_fkey");
        });

        modelBuilder.Entity<TokenTransaction>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("token_transactions_pkey");

            entity.ToTable("token_transactions", "billing", t => t.HasCheckConstraint(
                "chk_token_transactions_type",
                "type IN ('TopUp', 'Consume', 'Adjustment', 'Expire', 'Refund')"));

            entity.HasIndex(e => e.WorkspaceId, "idx_token_transactions_workspace");

            entity.HasIndex(e => new { e.WorkspaceId, e.CreatedAt })
                .HasDatabaseName("idx_token_transactions_workspace_created");

            entity.HasIndex(e => e.ReferenceId, "idx_token_transactions_reference");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuid_generate_v7()")
                .HasColumnName("id");
            entity.Property(e => e.Amount)
                .HasColumnName("amount");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy)
                .HasMaxLength(255)
                .HasColumnName("created_by");
            entity.Property(e => e.ReferenceId)
                .HasColumnName("reference_id");
            entity.Property(e => e.ReferenceType)
                .HasMaxLength(50)
                .HasColumnName("reference_type");
            entity.Property(e => e.Type)
                .HasMaxLength(20)
                .HasConversion<string>()
                .HasColumnName("type");
            entity.Property(e => e.WorkspaceId).HasColumnName("workspace_id");
        });

        modelBuilder.Entity<IdempotencyRecord>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("idempotency_records_pkey");

            entity.ToTable("idempotency_records", "billing");

            entity.HasIndex(e => new { e.Key, e.Operation }, "ux_idempotency_records_key_operation").IsUnique();
            entity.HasIndex(e => e.WorkspaceId, "idx_idempotency_records_workspace");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuid_generate_v7()")
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
