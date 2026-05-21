using Microsoft.EntityFrameworkCore;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Enums;

namespace WarpTalk.BillingService.Infrastructure.Persistence.Contexts;

public partial class BillingDbContext
{
    partial void OnModelCreatingPartial(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Plan>()
            .HasQueryFilter(p => p.DeletedAt == null);

        modelBuilder.Entity<Subscription>()
            .HasQueryFilter(s => s.DeletedAt == null);

        modelBuilder.Entity<Subscription>(entity =>
        {
            entity.Property(e => e.Status)
                .HasColumnName("status")
                .HasConversion(
                    v => v.ToLowerInvariant(),
                    v => Enum.Parse<SubscriptionStatus>(v, true))
                .HasDefaultValue(SubscriptionStatus.Active);
        });

        modelBuilder.Entity<Payment>(entity =>
        {
            entity.Property(e => e.Status)
                .HasColumnName("status")
                .HasConversion(
                    v => v.ToLowerInvariant(),
                    v => Enum.Parse<PaymentStatus>(v, true))
                .HasDefaultValue(PaymentStatus.Pending);
        });

        modelBuilder.Entity<CreditTransaction>(entity =>
        {
            entity.Property(e => e.Type)
                .HasColumnName("type")
                .HasConversion(
                    v => v.ToLowerInvariant(),
                    v => Enum.Parse<CreditTransactionType>(v, true));
        });

        modelBuilder.Entity<Refund>(entity =>
        {
            entity.Property(e => e.Status)
                .HasColumnName("status")
                .HasDefaultValue("pending");
        });

        modelBuilder.Entity<Invoice>(entity =>
        {
            entity.Property(e => e.Status)
                .HasColumnName("status")
                .HasDefaultValue("issued");
        });
    }
}
