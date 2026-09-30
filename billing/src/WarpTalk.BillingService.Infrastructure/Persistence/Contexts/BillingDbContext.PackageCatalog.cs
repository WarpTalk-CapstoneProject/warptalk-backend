using Microsoft.EntityFrameworkCore;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Infrastructure.Persistence;

/// <summary>
/// G11 — mapping of the tables migration 20260925160000 creates. Every column is mapped by name
/// (HasColumnName): this context has no naming convention, and a column EF guesses wrong 500s
/// every SELECT over the table.
/// </summary>
public partial class BillingDbContext
{
    public DbSet<CreditPack> CreditPacks => Set<CreditPack>();
    public DbSet<Addon> Addons => Set<Addon>();
    public DbSet<WorkspaceAddon> WorkspaceAddons => Set<WorkspaceAddon>();
    public DbSet<Coupon> Coupons => Set<Coupon>();
    public DbSet<CouponRedemption> CouponRedemptions => Set<CouponRedemption>();
    public DbSet<CreditPackPurchase> CreditPackPurchases => Set<CreditPackPurchase>();

    private static void ConfigurePackageCatalog(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CreditPack>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("credit_packs_pkey");
            entity.ToTable("credit_packs", "subscription");
            entity.HasIndex(e => e.Slug).IsUnique().HasDatabaseName("ux_credit_packs_slug");
            entity.Ignore(e => e.TotalCredits);

            entity.Property(e => e.Id).HasColumnName("id").HasDefaultValueSql("uuidv7()");
            entity.Property(e => e.Slug).HasColumnName("slug").HasMaxLength(60);
            entity.Property(e => e.Name).HasColumnName("name").HasMaxLength(100);
            entity.Property(e => e.Description).HasColumnName("description").HasMaxLength(500);
            entity.Property(e => e.Credits).HasColumnName("credits");
            entity.Property(e => e.BonusCredits).HasColumnName("bonus_credits");
            entity.Property(e => e.PriceVnd).HasColumnName("price_vnd").HasPrecision(14, 0);
            entity.Property(e => e.PriceUsd).HasColumnName("price_usd").HasPrecision(12, 2);
            entity.Property(e => e.ValidityDays).HasColumnName("validity_days");
            entity.Property(e => e.Visibility).HasColumnName("visibility").HasMaxLength(20);
            entity.Property(e => e.EligiblePlanIds).HasColumnName("eligible_plan_ids").HasColumnType("uuid[]");
            entity.Property(e => e.EligibleWorkspaceIds).HasColumnName("eligible_workspace_ids").HasColumnType("uuid[]");
            entity.Property(e => e.MaxPerWorkspace).HasColumnName("max_per_workspace");
            entity.Property(e => e.MaxTotal).HasColumnName("max_total");
            entity.Property(e => e.AvailableFrom).HasColumnName("available_from");
            entity.Property(e => e.AvailableUntil).HasColumnName("available_until");
            entity.Property(e => e.Status).HasColumnName("status").HasMaxLength(20);
            entity.Property(e => e.SortOrder).HasColumnName("sort_order");
            MapStripe(entity);
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.ArchivedAt).HasColumnName("archived_at");
        });

        modelBuilder.Entity<Addon>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("addons_pkey");
            entity.ToTable("addons", "subscription");
            entity.HasIndex(e => e.Slug).IsUnique().HasDatabaseName("ux_addons_slug");

            entity.Property(e => e.Id).HasColumnName("id").HasDefaultValueSql("uuidv7()");
            entity.Property(e => e.Slug).HasColumnName("slug").HasMaxLength(60);
            entity.Property(e => e.Name).HasColumnName("name").HasMaxLength(100);
            entity.Property(e => e.Description).HasColumnName("description").HasMaxLength(500);
            entity.Property(e => e.UnitLabel).HasColumnName("unit_label").HasMaxLength(40);
            entity.Property(e => e.EntitlementKey).HasColumnName("entitlement_key").HasMaxLength(50);
            entity.Property(e => e.UnitsPerQuantity).HasColumnName("units_per_quantity");
            entity.Property(e => e.PriceMonthlyVnd).HasColumnName("price_monthly_vnd").HasPrecision(14, 0);
            entity.Property(e => e.PriceYearlyVnd).HasColumnName("price_yearly_vnd").HasPrecision(14, 0);
            entity.Property(e => e.PriceMonthlyUsd).HasColumnName("price_monthly_usd").HasPrecision(12, 2);
            entity.Property(e => e.PriceYearlyUsd).HasColumnName("price_yearly_usd").HasPrecision(12, 2);
            entity.Property(e => e.MinQuantity).HasColumnName("min_quantity");
            entity.Property(e => e.MaxQuantity).HasColumnName("max_quantity");
            entity.Property(e => e.EligiblePlanIds).HasColumnName("eligible_plan_ids").HasColumnType("uuid[]");
            entity.Property(e => e.Status).HasColumnName("status").HasMaxLength(20);
            entity.Property(e => e.SortOrder).HasColumnName("sort_order");
            MapStripe(entity);
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.ArchivedAt).HasColumnName("archived_at");
        });

        modelBuilder.Entity<WorkspaceAddon>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("workspace_addons_pkey");
            entity.ToTable("workspace_addons", "subscription");
            entity.HasIndex(e => e.WorkspaceId).HasDatabaseName("ix_workspace_addons_workspace");
            entity.HasIndex(e => e.StripeSubscriptionId).IsUnique().HasDatabaseName("ux_workspace_addons_stripe_subscription");
            entity.HasIndex(e => e.StripeSessionId).IsUnique().HasDatabaseName("ux_workspace_addons_stripe_session");
            entity.HasOne(e => e.Addon).WithMany().HasForeignKey(e => e.AddonId).HasConstraintName("fk_workspace_addons_addon");

            entity.Property(e => e.Id).HasColumnName("id").HasDefaultValueSql("uuidv7()");
            entity.Property(e => e.WorkspaceId).HasColumnName("workspace_id");
            entity.Property(e => e.AddonId).HasColumnName("addon_id");
            entity.Property(e => e.UserId).HasColumnName("user_id");
            entity.Property(e => e.Quantity).HasColumnName("quantity");
            entity.Property(e => e.BillingCycle).HasColumnName("billing_cycle").HasMaxLength(20);
            entity.Property(e => e.Currency).HasColumnName("currency").HasMaxLength(3);
            entity.Property(e => e.UnitPrice).HasColumnName("unit_price").HasPrecision(14, 2);
            entity.Property(e => e.AmountBilledTotal).HasColumnName("amount_billed_total").HasPrecision(16, 2);
            entity.Property(e => e.Status).HasColumnName("status").HasMaxLength(20);
            entity.Property(e => e.StripeSubscriptionId).HasColumnName("stripe_subscription_id").HasMaxLength(255);
            entity.Property(e => e.StripeSessionId).HasColumnName("stripe_session_id").HasMaxLength(255);
            entity.Property(e => e.CouponId).HasColumnName("coupon_id");
            entity.Property(e => e.CurrentPeriodEnd).HasColumnName("current_period_end");
            entity.Property(e => e.StartedAt).HasColumnName("started_at");
            entity.Property(e => e.CancelledAt).HasColumnName("cancelled_at");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<Coupon>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("coupons_pkey");
            entity.ToTable("coupons", "subscription");
            entity.HasIndex(e => e.Code).IsUnique().HasDatabaseName("ux_coupons_code");

            entity.Property(e => e.Id).HasColumnName("id").HasDefaultValueSql("uuidv7()");
            entity.Property(e => e.Code).HasColumnName("code").HasMaxLength(40);
            entity.Property(e => e.Name).HasColumnName("name").HasMaxLength(100);
            entity.Property(e => e.DiscountType).HasColumnName("discount_type").HasMaxLength(20);
            entity.Property(e => e.PercentOff).HasColumnName("percent_off").HasPrecision(5, 2);
            entity.Property(e => e.AmountOff).HasColumnName("amount_off").HasPrecision(14, 2);
            entity.Property(e => e.AmountOffCurrency).HasColumnName("amount_off_currency").HasMaxLength(3);
            entity.Property(e => e.AppliesToTypes).HasColumnName("applies_to_types").HasColumnType("text[]");
            entity.Property(e => e.AppliesToIds).HasColumnName("applies_to_ids").HasColumnType("uuid[]");
            entity.Property(e => e.Duration).HasColumnName("duration").HasMaxLength(20);
            entity.Property(e => e.DurationInMonths).HasColumnName("duration_in_months");
            entity.Property(e => e.MaxRedemptions).HasColumnName("max_redemptions");
            entity.Property(e => e.PerWorkspaceLimit).HasColumnName("per_workspace_limit");
            entity.Property(e => e.ValidFrom).HasColumnName("valid_from");
            entity.Property(e => e.ValidUntil).HasColumnName("valid_until");
            entity.Property(e => e.AutoApply).HasColumnName("auto_apply");
            entity.Property(e => e.Status).HasColumnName("status").HasMaxLength(20);
            entity.Property(e => e.StripeCouponId).HasColumnName("stripe_coupon_id").HasMaxLength(255);
            entity.Property(e => e.StripePromotionCodeId).HasColumnName("stripe_promotion_code_id").HasMaxLength(255);
            entity.Property(e => e.StripeSyncedAt).HasColumnName("stripe_synced_at");
            entity.Property(e => e.StripeSyncError).HasColumnName("stripe_sync_error").HasMaxLength(500);
            entity.Property(e => e.StripeSyncedHash).HasColumnName("stripe_synced_hash").HasMaxLength(64);
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.ArchivedAt).HasColumnName("archived_at");
        });

        modelBuilder.Entity<CouponRedemption>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("coupon_redemptions_pkey");
            entity.ToTable("coupon_redemptions", "subscription");
            entity.HasIndex(e => e.StripeSessionId).IsUnique().HasDatabaseName("ux_coupon_redemptions_session");
            entity.HasIndex(e => new { e.CouponId, e.WorkspaceId }).HasDatabaseName("ix_coupon_redemptions_coupon_workspace");

            entity.Property(e => e.Id).HasColumnName("id").HasDefaultValueSql("uuidv7()");
            entity.Property(e => e.CouponId).HasColumnName("coupon_id");
            entity.Property(e => e.WorkspaceId).HasColumnName("workspace_id");
            entity.Property(e => e.UserId).HasColumnName("user_id");
            entity.Property(e => e.ItemType).HasColumnName("item_type").HasMaxLength(20);
            entity.Property(e => e.ItemId).HasColumnName("item_id");
            entity.Property(e => e.StripeSessionId).HasColumnName("stripe_session_id").HasMaxLength(255);
            entity.Property(e => e.PaymentId).HasColumnName("payment_id");
            entity.Property(e => e.Currency).HasColumnName("currency").HasMaxLength(3);
            entity.Property(e => e.DiscountAmount).HasColumnName("discount_amount").HasPrecision(14, 2);
            entity.Property(e => e.RedeemedAt).HasColumnName("redeemed_at");
        });

        modelBuilder.Entity<CreditPackPurchase>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("credit_pack_purchases_pkey");
            entity.ToTable("credit_pack_purchases", "subscription");
            entity.HasIndex(e => e.StripeSessionId).IsUnique().HasDatabaseName("ux_credit_pack_purchases_session");
            entity.HasIndex(e => new { e.CreditPackId, e.WorkspaceId }).HasDatabaseName("ix_credit_pack_purchases_pack_workspace");
            entity.Ignore(e => e.TotalCredits);

            entity.Property(e => e.Id).HasColumnName("id").HasDefaultValueSql("uuidv7()");
            entity.Property(e => e.CreditPackId).HasColumnName("credit_pack_id");
            entity.Property(e => e.WorkspaceId).HasColumnName("workspace_id");
            entity.Property(e => e.UserId).HasColumnName("user_id");
            entity.Property(e => e.SubscriptionId).HasColumnName("subscription_id");
            entity.Property(e => e.PaymentId).HasColumnName("payment_id");
            entity.Property(e => e.StripeSessionId).HasColumnName("stripe_session_id").HasMaxLength(255);
            entity.Property(e => e.Credits).HasColumnName("credits");
            entity.Property(e => e.BonusCredits).HasColumnName("bonus_credits");
            entity.Property(e => e.Currency).HasColumnName("currency").HasMaxLength(3);
            entity.Property(e => e.AmountPaid).HasColumnName("amount_paid").HasPrecision(14, 2);
            entity.Property(e => e.CouponId).HasColumnName("coupon_id");
            entity.Property(e => e.DiscountAmount).HasColumnName("discount_amount").HasPrecision(14, 2);
            entity.Property(e => e.PurchasedAt).HasColumnName("purchased_at");
            entity.Property(e => e.ExpiresAt).HasColumnName("expires_at");
            entity.Property(e => e.ExpiredCredits).HasColumnName("expired_credits");
            entity.Property(e => e.ExpiredAt).HasColumnName("expired_at");
        });
    }

    private static void MapStripe<T>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<T> entity)
        where T : class, IStripeSyncedCatalogItem
    {
        // By name, not by lambda: a lambda through the interface constraint is not a member
        // access EF can resolve to the entity's own property.
        entity.Property(nameof(IStripeSyncedCatalogItem.StripeProductId)).HasColumnName("stripe_product_id").HasMaxLength(255);
        entity.Property(nameof(IStripeSyncedCatalogItem.StripePriceIds)).HasColumnName("stripe_price_ids").HasColumnType("jsonb");
        entity.Property(nameof(IStripeSyncedCatalogItem.StripeSyncedAt)).HasColumnName("stripe_synced_at");
        entity.Property(nameof(IStripeSyncedCatalogItem.StripeSyncError)).HasColumnName("stripe_sync_error").HasMaxLength(500);
        entity.Property(nameof(IStripeSyncedCatalogItem.StripeSyncedHash)).HasColumnName("stripe_synced_hash").HasMaxLength(64);
    }
}
