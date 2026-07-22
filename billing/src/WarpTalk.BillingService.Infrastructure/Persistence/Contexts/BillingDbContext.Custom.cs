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
