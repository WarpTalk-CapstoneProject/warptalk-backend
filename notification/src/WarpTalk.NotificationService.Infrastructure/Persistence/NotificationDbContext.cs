using WarpTalk.NotificationService.Domain.Entities;
using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
// using WarpTalk.NotificationService.Infrastructure.Entities;

namespace WarpTalk.NotificationService.Infrastructure.Persistence;

public partial class NotificationDbContext : DbContext
{
    public NotificationDbContext()
    {
    }

    public NotificationDbContext(DbContextOptions<NotificationDbContext> options)
        : base(options)
    {
    }

    public virtual DbSet<NotificationPreference> NotificationPreferences { get; set; }

    public virtual DbSet<NotificationTemplate> NotificationTemplates { get; set; }

    public virtual DbSet<PushSubscription> PushSubscriptions { get; set; }

    public virtual DbSet<NotificationMessage> NotificationMessages { get; set; }

    public virtual DbSet<AdminNotification> AdminNotifications { get; set; }
    public virtual DbSet<NotificationInboxMessage> InboxMessages { get; set; }
    public virtual DbSet<Announcement> Announcements { get; set; }
    public virtual DbSet<AnnouncementViewerState> AnnouncementViewerStates { get; set; }
    public virtual DbSet<AnnouncementDailyStat> AnnouncementDailyStats { get; set; }
    public virtual DbSet<AnnouncementAsset> AnnouncementAssets { get; set; }
    public virtual DbSet<EmailBlock> EmailBlocks { get; set; }
    public virtual DbSet<EmailContentVariant> EmailContentVariants { get; set; }
    public virtual DbSet<EmailCmsVersion> EmailCmsVersions { get; set; }
    public virtual DbSet<EmailSampleDataSet> EmailSampleDataSets { get; set; }
    public virtual DbSet<EmailDeliveryStat> EmailDeliveryStats { get; set; }
    public virtual DbSet<EmailCustomTemplate> EmailCustomTemplates { get; set; }
    public virtual DbSet<EmailCampaign> EmailCampaigns { get; set; }
    public virtual DbSet<EmailCampaignRecipient> EmailCampaignRecipients { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("pgcrypto");
        modelBuilder.HasPostgresExtension("pg_trgm");

        modelBuilder.Entity<AdminNotification>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("admin_notifications_pkey");

            entity.ToTable("admin_notifications", "notification");

            entity.HasIndex(e => e.CreatedAt, "idx_admin_notifications_created_at").IsDescending();
            entity.HasIndex(e => e.CreatedBy, "idx_admin_notifications_created_by");
            entity.HasIndex(e => e.Status, "idx_admin_notifications_status");
            entity.HasIndex(e => new { e.Type, e.Status, e.CreatedAt }, "idx_admin_notif_list_opt").IsDescending(false, false, true);

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuid_generate_v7()")
                .HasColumnName("id");
            entity.Property(e => e.Title)
                .HasMaxLength(255)
                .HasColumnName("title");
            entity.Property(e => e.Content).HasColumnName("content");
            entity.Property(e => e.Type)
                .HasMaxLength(50)
                .HasColumnName("type");
            entity.Property(e => e.Payload)
                .HasDefaultValueSql("'{}'::jsonb")
                .HasColumnType("jsonb")
                .HasColumnName("payload");
            entity.Property(e => e.TargetAudienceMode)
                .HasMaxLength(50)
                .HasColumnName("target_audience_mode");
            entity.Property(e => e.TargetAudienceData)
                .HasDefaultValueSql("'{}'::jsonb")
                .HasColumnType("jsonb")
                .HasColumnName("target_audience_data");
            entity.Property(e => e.Status)
                .HasMaxLength(50)
                .HasColumnName("status");
            entity.Property(e => e.SentAt).HasColumnName("sent_at");
            entity.Property(e => e.DeliveredCount).HasColumnName("delivered_count");
            entity.Property(e => e.DeliveryChunkCount).HasColumnName("delivery_chunk_count");
            entity.Property(e => e.DeliveredChunkCount).HasColumnName("delivered_chunk_count");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");
        });

        modelBuilder.Entity<NotificationInboxMessage>(entity =>
        {
            entity.HasKey(e => new { e.EventId, e.Consumer }).HasName("inbox_messages_pkey");
            entity.ToTable("inbox_messages", "notification");
            entity.Property(e => e.EventId).HasColumnName("event_id");
            entity.Property(e => e.Consumer).HasColumnName("consumer").HasMaxLength(150);
            entity.Property(e => e.EventType).HasColumnName("event_type").HasMaxLength(150);
            entity.Property(e => e.ProcessedAt).HasColumnName("processed_at");
        });

        modelBuilder.Entity<NotificationMessage>(entity =>
        {
            entity.HasKey(e => new { e.Id, e.CreatedAt }).HasName("notification_messages_pkey");

            entity.ToTable("notification_messages", "notification");

            entity.HasIndex(e => new { e.UserId, e.CreatedAt }, "idx_notif_msgs_user_unread").IsDescending(false, true).HasFilter("(is_read = FALSE)");
            entity.HasIndex(e => e.CreatedAt, "idx_notif_msgs_created_at").IsDescending();
            entity.HasIndex(e => e.UserId, "idx_notif_msgs_user");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuid_generate_v7()")
                .HasColumnName("id");
            entity.Property(e => e.UserId).HasColumnName("user_id");
            entity.Property(e => e.Type)
                .HasMaxLength(50)
                .HasColumnName("type");
            entity.Property(e => e.Title)
                .HasMaxLength(255)
                .HasColumnName("title");
            entity.Property(e => e.Content).HasColumnName("content");
            entity.Property(e => e.ActionUrl)
                .HasMaxLength(500)
                .HasColumnName("action_url");
            entity.Property(e => e.PayloadJson)
                .HasDefaultValueSql("'{}'::jsonb")
                .HasColumnType("jsonb")
                .HasColumnName("payload_json");
            entity.Property(e => e.IsRead)
                .HasDefaultValue(false)
                .HasColumnName("is_read");
            entity.Property(e => e.ReadAt).HasColumnName("read_at");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
        });

        modelBuilder.Entity<NotificationPreference>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("notification_preferences_pkey");

            entity.ToTable("notification_preferences", "notification");

            entity.HasIndex(e => e.UserId, "idx_notif_prefs_user");

            entity.HasIndex(e => new { e.UserId, e.NotificationType }, "notification_preferences_user_id_notification_type_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuid_generate_v7()")
                .HasColumnName("id");
            entity.Property(e => e.EmailEnabled)
                .HasDefaultValue(true)
                .HasColumnName("email_enabled");
            entity.Property(e => e.InAppEnabled)
                .HasDefaultValue(true)
                .HasColumnName("in_app_enabled");
            entity.Property(e => e.NotificationType)
                .HasMaxLength(50)
                .HasColumnName("notification_type");
            entity.Property(e => e.PushEnabled)
                .HasDefaultValue(true)
                .HasColumnName("push_enabled");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");
            entity.Property(e => e.UserId).HasColumnName("user_id");
        });

        modelBuilder.Entity<NotificationTemplate>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("notification_templates_pkey");

            entity.ToTable("notification_templates", "notification");

            entity.HasIndex(e => e.Type, "notification_templates_type_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuid_generate_v7()")
                .HasColumnName("id");
            entity.Property(e => e.BodyTemplate).HasColumnName("body_template");
            entity.Property(e => e.Channel)
                .HasMaxLength(20)
                .HasColumnName("channel");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true)
                .HasColumnName("is_active");
            entity.Property(e => e.Subject)
                .HasMaxLength(255)
                .HasColumnName("subject");
            entity.Property(e => e.Type)
                .HasMaxLength(50)
                .HasColumnName("type");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");
            entity.Property(e => e.Variables)
                .HasDefaultValueSql("'[]'::jsonb")
                .HasColumnType("jsonb")
                .HasColumnName("variables");
            // Email template CMS (20260924100000_email_template_cms_and_announcements.sql).
            entity.Property(e => e.Heading)
                .HasMaxLength(255)
                .HasColumnName("heading");
            entity.Property(e => e.Version).HasColumnName("version");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<Announcement>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("announcements_pkey");

            entity.ToTable("announcements", "notification");

            entity.HasIndex(e => new { e.Status, e.StartsAt, e.EndsAt }, "idx_announcements_status_window");
            entity.HasIndex(e => e.CreatedAt, "idx_announcements_created_at").IsDescending();

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Title)
                .HasMaxLength(200)
                .HasColumnName("title");
            entity.Property(e => e.BodyMarkdown).HasColumnName("body_markdown");
            entity.Property(e => e.Type)
                .HasMaxLength(30)
                .HasColumnName("type");
            entity.Property(e => e.Status)
                .HasMaxLength(20)
                .HasColumnName("status");
            entity.Property(e => e.AudienceMode)
                .HasMaxLength(20)
                .HasColumnName("audience_mode");
            entity.Property(e => e.AudiencePlanSlugs)
                .HasColumnType("text[]")
                .HasColumnName("audience_plan_slugs");
            entity.Property(e => e.AudienceWorkspaceIds)
                .HasColumnType("uuid[]")
                .HasColumnName("audience_workspace_ids");
            entity.Property(e => e.Placement).HasMaxLength(30).HasColumnName("placement");
            entity.Property(e => e.Variant).HasMaxLength(20).HasColumnName("variant");
            entity.Property(e => e.AccentColor).HasMaxLength(20).HasColumnName("accent_color");
            entity.Property(e => e.Icon).HasMaxLength(40).HasColumnName("icon");
            entity.Property(e => e.ImageUrl).HasMaxLength(2048).HasColumnName("image_url");
            entity.Property(e => e.Priority).HasColumnName("priority");
            entity.Property(e => e.Dismissible).HasColumnName("dismissible");
            entity.Property(e => e.Frequency).HasMaxLength(20).HasColumnName("frequency");
            entity.Property(e => e.TargetRoles).HasColumnType("text[]").HasColumnName("target_roles");
            entity.Property(e => e.TargetLocales).HasColumnType("text[]").HasColumnName("target_locales");
            entity.Property(e => e.NewUsersWithinDays).HasColumnName("new_users_within_days");
            entity.Property(e => e.SecondaryCtaLabel).HasMaxLength(60).HasColumnName("secondary_cta_label");
            entity.Property(e => e.SecondaryCtaUrl).HasMaxLength(2048).HasColumnName("secondary_cta_url");
            entity.Property(e => e.EmailTemplateKey).HasMaxLength(60).HasColumnName("email_template_key");
            entity.Property(e => e.EmailCampaignId).HasColumnName("email_campaign_id");
            entity.Property(e => e.CtaLabel)
                .HasMaxLength(60)
                .HasColumnName("cta_label");
            entity.Property(e => e.CtaUrl)
                .HasMaxLength(2048)
                .HasColumnName("cta_url");
            entity.Property(e => e.StartsAt).HasColumnName("starts_at");
            entity.Property(e => e.EndsAt).HasColumnName("ends_at");
            entity.Property(e => e.PublishedAt).HasColumnName("published_at");
            entity.Property(e => e.PublishedBy).HasColumnName("published_by");
            entity.Property(e => e.ArchivedAt).HasColumnName("archived_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<AnnouncementViewerState>(entity =>
        {
            entity.HasKey(e => new { e.AnnouncementId, e.UserId }).HasName("announcement_viewer_states_pkey");

            entity.ToTable("announcement_viewer_states", "notification");

            entity.Property(e => e.AnnouncementId).HasColumnName("announcement_id");
            entity.Property(e => e.UserId).HasColumnName("user_id");
            entity.Property(e => e.ImpressionCount).HasColumnName("impression_count");
            entity.Property(e => e.FirstSeenAt).HasColumnName("first_seen_at");
            entity.Property(e => e.LastSeenAt).HasColumnName("last_seen_at");
            entity.Property(e => e.LastSessionId).HasMaxLength(64).HasColumnName("last_session_id");
            entity.Property(e => e.DismissedAt).HasColumnName("dismissed_at");
            entity.Property(e => e.DismissedSessionId).HasMaxLength(64).HasColumnName("dismissed_session_id");
            entity.Property(e => e.CtaClickCount).HasColumnName("cta_click_count");
            entity.Property(e => e.SecondaryClickCount).HasColumnName("secondary_click_count");
            entity.Property(e => e.LastClickedAt).HasColumnName("last_clicked_at");
        });

        modelBuilder.Entity<AnnouncementDailyStat>(entity =>
        {
            entity.HasKey(e => new { e.AnnouncementId, e.Day }).HasName("announcement_daily_stats_pkey");

            entity.ToTable("announcement_daily_stats", "notification");

            entity.Property(e => e.AnnouncementId).HasColumnName("announcement_id");
            entity.Property(e => e.Day).HasColumnName("day");
            entity.Property(e => e.Impressions).HasColumnName("impressions");
            entity.Property(e => e.Dismissals).HasColumnName("dismissals");
            entity.Property(e => e.CtaClicks).HasColumnName("cta_clicks");
            entity.Property(e => e.SecondaryClicks).HasColumnName("secondary_clicks");
        });

        modelBuilder.Entity<AnnouncementAsset>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("announcement_assets_pkey");

            entity.ToTable("announcement_assets", "notification");

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.FileName).HasMaxLength(255).HasColumnName("file_name");
            entity.Property(e => e.ContentType).HasMaxLength(50).HasColumnName("content_type");
            entity.Property(e => e.SizeBytes).HasColumnName("size_bytes");
            entity.Property(e => e.Content).HasColumnName("content");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");
        });

        modelBuilder.Entity<EmailBlock>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("email_blocks_pkey");

            entity.ToTable("email_blocks", "notification");

            entity.HasIndex(e => new { e.Kind, e.Key }, "uq_email_blocks_kind_key").IsUnique();

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Kind).HasMaxLength(20).HasColumnName("kind");
            entity.Property(e => e.Key).HasMaxLength(60).HasColumnName("key");
            entity.Property(e => e.Name).HasMaxLength(120).HasColumnName("name");
            entity.Property(e => e.Description).HasMaxLength(500).HasColumnName("description");
            entity.Property(e => e.Status).HasMaxLength(20).HasColumnName("status");
            entity.Property(e => e.IsDefault).HasColumnName("is_default");
            entity.Property(e => e.DraftHtml).HasColumnName("draft_html");
            entity.Property(e => e.DraftText).HasColumnName("draft_text");
            entity.Property(e => e.DraftDarkCss).HasColumnName("draft_dark_css");
            entity.Property(e => e.PublishedHtml).HasColumnName("published_html");
            entity.Property(e => e.PublishedText).HasColumnName("published_text");
            entity.Property(e => e.PublishedDarkCss).HasColumnName("published_dark_css");
            entity.Property(e => e.PublishedVersion).HasColumnName("published_version");
            entity.Property(e => e.PublishedAt).HasColumnName("published_at");
            entity.Property(e => e.PublishedBy).HasColumnName("published_by");
            entity.Property(e => e.DraftUpdatedAt).HasColumnName("draft_updated_at");
            entity.Property(e => e.DraftUpdatedBy).HasColumnName("draft_updated_by");
            entity.Property(e => e.ArchivedAt).HasColumnName("archived_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");
        });

        modelBuilder.Entity<EmailContentVariant>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("email_content_variants_pkey");

            entity.ToTable("email_content_variants", "notification");

            entity.HasIndex(e => new { e.TemplateKey, e.Locale }, "uq_email_content_variants_key_locale").IsUnique();

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.TemplateKey).HasMaxLength(60).HasColumnName("template_key");
            entity.Property(e => e.Locale).HasMaxLength(10).HasColumnName("locale");
            entity.Property(e => e.Status).HasMaxLength(20).HasColumnName("status");
            entity.Property(e => e.DraftSubject).HasMaxLength(255).HasColumnName("draft_subject");
            entity.Property(e => e.DraftPreheader).HasMaxLength(255).HasColumnName("draft_preheader");
            entity.Property(e => e.DraftHeading).HasMaxLength(255).HasColumnName("draft_heading");
            entity.Property(e => e.DraftBodyHtml).HasColumnName("draft_body_html");
            entity.Property(e => e.DraftTextBody).HasColumnName("draft_text_body");
            entity.Property(e => e.DraftLayoutId).HasColumnName("draft_layout_id");
            entity.Property(e => e.PublishedSubject).HasMaxLength(255).HasColumnName("published_subject");
            entity.Property(e => e.PublishedPreheader).HasMaxLength(255).HasColumnName("published_preheader");
            entity.Property(e => e.PublishedHeading).HasMaxLength(255).HasColumnName("published_heading");
            entity.Property(e => e.PublishedBodyHtml).HasColumnName("published_body_html");
            entity.Property(e => e.PublishedTextBody).HasColumnName("published_text_body");
            entity.Property(e => e.PublishedLayoutId).HasColumnName("published_layout_id");
            entity.Property(e => e.PublishedVersion).HasColumnName("published_version");
            entity.Property(e => e.PublishedAt).HasColumnName("published_at");
            entity.Property(e => e.PublishedBy).HasColumnName("published_by");
            entity.Property(e => e.DraftUpdatedAt).HasColumnName("draft_updated_at");
            entity.Property(e => e.DraftUpdatedBy).HasColumnName("draft_updated_by");
            entity.Property(e => e.ArchivedAt).HasColumnName("archived_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");
        });

        modelBuilder.Entity<EmailCmsVersion>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("email_cms_versions_pkey");

            entity.ToTable("email_cms_versions", "notification");

            entity.HasIndex(e => new { e.OwnerType, e.OwnerId, e.Version }, "uq_email_cms_versions_owner_version").IsUnique();

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.OwnerType).HasMaxLength(20).HasColumnName("owner_type");
            entity.Property(e => e.OwnerId).HasColumnName("owner_id");
            entity.Property(e => e.Version).HasColumnName("version");
            entity.Property(e => e.Action).HasMaxLength(20).HasColumnName("action");
            entity.Property(e => e.Snapshot).HasColumnType("jsonb").HasColumnName("snapshot");
            entity.Property(e => e.Note).HasMaxLength(500).HasColumnName("note");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");
        });

        modelBuilder.Entity<EmailSampleDataSet>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("email_sample_data_sets_pkey");

            entity.ToTable("email_sample_data_sets", "notification");

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.TemplateKey).HasMaxLength(60).HasColumnName("template_key");
            entity.Property(e => e.Name).HasMaxLength(120).HasColumnName("name");
            entity.Property(e => e.Values).HasColumnType("jsonb").HasColumnName("values");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<EmailCustomTemplate>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("email_custom_templates_pkey");

            entity.ToTable("email_custom_templates", "notification");

            entity.HasIndex(e => e.Key, "uq_email_custom_templates_key").IsUnique();

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Key).HasMaxLength(60).HasColumnName("key");
            entity.Property(e => e.Name).HasMaxLength(120).HasColumnName("name");
            entity.Property(e => e.Description).HasMaxLength(500).HasColumnName("description");
            entity.Property(e => e.Category).HasMaxLength(30).HasColumnName("category");
            entity.Property(e => e.Variables).HasColumnType("jsonb").HasColumnName("variables");
            entity.Property(e => e.Status).HasMaxLength(20).HasColumnName("status");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.DeleteReason).HasMaxLength(500).HasColumnName("delete_reason");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<EmailCampaign>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("email_campaigns_pkey");

            entity.ToTable("email_campaigns", "notification");

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.TemplateKey).HasMaxLength(60).HasColumnName("template_key");
            entity.Property(e => e.Source).HasMaxLength(20).HasColumnName("source");
            entity.Property(e => e.AnnouncementId).HasColumnName("announcement_id");
            entity.Property(e => e.Audience).HasColumnType("jsonb").HasColumnName("audience");
            entity.Property(e => e.Values).HasColumnType("jsonb").HasColumnName("values");
            entity.Property(e => e.Status).HasMaxLength(20).HasColumnName("status");
            entity.Property(e => e.ScheduledAt).HasColumnName("scheduled_at");
            entity.Property(e => e.StartedAt).HasColumnName("started_at");
            entity.Property(e => e.CompletedAt).HasColumnName("completed_at");
            entity.Property(e => e.TotalCount).HasColumnName("total_count");
            entity.Property(e => e.SentCount).HasColumnName("sent_count");
            entity.Property(e => e.FailedCount).HasColumnName("failed_count");
            entity.Property(e => e.SkippedCount).HasColumnName("skipped_count");
            entity.Property(e => e.Error).HasMaxLength(500).HasColumnName("error");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");
            entity.Property(e => e.CancelledBy).HasColumnName("cancelled_by");
            entity.Property(e => e.CancelledAt).HasColumnName("cancelled_at");
        });

        modelBuilder.Entity<EmailCampaignRecipient>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("email_campaign_recipients_pkey");

            entity.ToTable("email_campaign_recipients", "notification");

            entity.HasIndex(e => new { e.CampaignId, e.UserId }, "uq_email_campaign_recipients_user").IsUnique();

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.CampaignId).HasColumnName("campaign_id");
            entity.Property(e => e.UserId).HasColumnName("user_id");
            entity.Property(e => e.Email).HasMaxLength(320).HasColumnName("email");
            entity.Property(e => e.FullName).HasMaxLength(200).HasColumnName("full_name");
            entity.Property(e => e.Locale).HasMaxLength(5).HasColumnName("locale");
            entity.Property(e => e.Status).HasMaxLength(20).HasColumnName("status");
            entity.Property(e => e.Error).HasMaxLength(500).HasColumnName("error");
            entity.Property(e => e.SentAt).HasColumnName("sent_at");
        });

        modelBuilder.Entity<EmailDeliveryStat>(entity =>
        {
            entity.HasKey(e => new { e.TemplateKey, e.Locale, e.Day }).HasName("email_delivery_stats_pkey");

            entity.ToTable("email_delivery_stats", "notification");

            entity.Property(e => e.TemplateKey).HasMaxLength(60).HasColumnName("template_key");
            entity.Property(e => e.Locale).HasMaxLength(10).HasColumnName("locale");
            entity.Property(e => e.Day).HasColumnName("day");
            entity.Property(e => e.SentCount).HasColumnName("sent_count");
            entity.Property(e => e.FailedCount).HasColumnName("failed_count");
        });

        modelBuilder.Entity<PushSubscription>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("push_subscriptions_pkey");

            entity.ToTable("push_subscriptions", "notification");

            entity.HasIndex(e => e.UserId, "idx_push_subs_user");

            entity.HasIndex(e => e.DeviceToken, "push_subscriptions_device_token_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuid_generate_v7()")
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.DeviceName)
                .HasMaxLength(100)
                .HasColumnName("device_name");
            entity.Property(e => e.DeviceToken)
                .HasMaxLength(500)
                .HasColumnName("device_token");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true)
                .HasColumnName("is_active");
            entity.Property(e => e.LastUsedAt).HasColumnName("last_used_at");
            entity.Property(e => e.Platform)
                .HasMaxLength(20)
                .HasColumnName("platform");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");
            entity.Property(e => e.UserId).HasColumnName("user_id");
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
