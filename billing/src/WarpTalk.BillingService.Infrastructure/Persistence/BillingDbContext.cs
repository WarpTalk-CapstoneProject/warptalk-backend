using Microsoft.EntityFrameworkCore;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Enums;
using System;

namespace WarpTalk.BillingService.Infrastructure.Persistence;

public class BillingDbContext : DbContext
{
    public BillingDbContext(DbContextOptions<BillingDbContext> options) : base(options)
    {
    }

    public DbSet<SubscriptionPlan> SubscriptionPlans { get; set; } = null!;
    public DbSet<UsageQuota> UsageQuotas { get; set; } = null!;
    public DbSet<Transaction> Transactions { get; set; } = null!;
    public DbSet<QuotaAuditLog> QuotaAuditLogs { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        
        // Đặt mặc định schema cho service Billing
        modelBuilder.HasDefaultSchema("billing");

        // SubscriptionPlan Configuration
        modelBuilder.Entity<SubscriptionPlan>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasConversion<string>();
            entity.Property(e => e.BaseQuotaMinutes).HasColumnType("decimal(18,4)");
            entity.Property(e => e.PriceVnd).HasColumnType("decimal(18,2)");
            entity.Property(e => e.FeaturesJson).HasColumnType("jsonb");

            // Seeding Default Plans
            entity.HasData(
                new SubscriptionPlan { Id = Guid.Parse("11111111-1111-1111-1111-111111111111"), Name = PlanType.Free, BaseQuotaMinutes = 30, PriceVnd = 0, MaxParticipants = 5 },
                new SubscriptionPlan { Id = Guid.Parse("22222222-2222-2222-2222-222222222222"), Name = PlanType.Pro, BaseQuotaMinutes = 500, PriceVnd = 199000, MaxParticipants = 25 },
                new SubscriptionPlan { Id = Guid.Parse("33333333-3333-3333-3333-333333333333"), Name = PlanType.Premium, BaseQuotaMinutes = 1000, PriceVnd = 499000, MaxParticipants = 100 },
                new SubscriptionPlan { Id = Guid.Parse("44444444-4444-4444-4444-444444444444"), Name = PlanType.Enterprise, BaseQuotaMinutes = 10000, PriceVnd = 0, MaxParticipants = 1000 }
            );
        });

        // UsageQuota Configuration
        modelBuilder.Entity<UsageQuota>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.WorkspaceId).IsUnique();
            entity.Property(e => e.TotalAllocatedMinutes).HasColumnType("decimal(18,4)");
            entity.Property(e => e.ConsumedMinutes).HasColumnType("decimal(18,4)");
            entity.Property(e => e.Version).IsRowVersion(); // Optimistic Concurrency

            // One-to-Many: SubscriptionPlan -> UsageQuotas
            entity.HasOne(e => e.Plan)
                  .WithMany()
                  .HasForeignKey(e => e.PlanId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        // Transaction Configuration
        modelBuilder.Entity<Transaction>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.OrderCode).IsUnique();
            entity.Property(e => e.AmountVnd).HasColumnType("decimal(18,2)");
            entity.Property(e => e.PurchasedMinutes).HasColumnType("decimal(18,4)");
            entity.Property(e => e.Status).IsRequired().HasConversion<string>();
        });

        // QuotaAuditLog Configuration
        modelBuilder.Entity<QuotaAuditLog>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.WorkspaceId);
            entity.HasIndex(e => e.ReferenceId); // Idempotency
            entity.Property(e => e.Action).IsRequired().HasConversion<string>();
            entity.Property(e => e.Amount).HasColumnType("decimal(18,4)");
            entity.Property(e => e.BalanceAfter).HasColumnType("decimal(18,4)");
        });
    }
}
