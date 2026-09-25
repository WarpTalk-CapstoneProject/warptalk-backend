using Microsoft.EntityFrameworkCore;
using WarpTalk.WorkspaceService.Domain.Entities;

namespace WarpTalk.WorkspaceService.Infrastructure.Persistence;

// Safe from re-scaffold: scaffold only writes WorkspaceDbContext.cs, never *.partial.cs.
public partial class WorkspaceDbContext
{
    public virtual DbSet<WorkspaceAdminAction> WorkspaceAdminActions { get; set; } = null!;

    /// <summary>WT-263: the replicated entitlement snapshot (migration 050).</summary>
    public virtual DbSet<WorkspaceEntitlementSnapshot> WorkspaceEntitlementSnapshots { get; set; } = null!;

    /// <summary>Internal admin notes on a workspace (migration 20260924090000).</summary>
    public virtual DbSet<WorkspaceAdminNote> WorkspaceAdminNotes { get; set; } = null!;

    /// <summary>G12 pending-work inbox triage (migration 20260925160000).</summary>
    public virtual DbSet<AdminInboxItemState> AdminInboxItemStates { get; set; } = null!;
    public virtual DbSet<AdminInboxNote> AdminInboxNotes { get; set; } = null!;

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder)
    {
        // G12: every column mapped by name — this context has no naming convention.
        modelBuilder.Entity<AdminInboxItemState>(entity =>
        {
            entity.HasKey(e => e.ItemKey).HasName("admin_inbox_item_states_pkey");
            entity.ToTable("admin_inbox_item_states", "workspace");
            entity.Property(e => e.ItemKey).HasColumnName("item_key").HasMaxLength(200);
            entity.Property(e => e.ItemType).HasColumnName("item_type").HasMaxLength(60);
            entity.Property(e => e.AssigneeId).HasColumnName("assignee_id");
            entity.Property(e => e.AssignedBy).HasColumnName("assigned_by");
            entity.Property(e => e.AssignedAt).HasColumnName("assigned_at");
            entity.Property(e => e.SnoozedUntil).HasColumnName("snoozed_until");
            entity.Property(e => e.SnoozedBy).HasColumnName("snoozed_by");
            entity.Property(e => e.DoneAt).HasColumnName("done_at");
            entity.Property(e => e.DoneBy).HasColumnName("done_by");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()").HasColumnName("created_at");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()").HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<AdminInboxNote>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("admin_inbox_notes_pkey");
            entity.ToTable("admin_inbox_notes", "workspace");
            entity.HasIndex(e => new { e.ItemKey, e.CreatedAt }, "idx_admin_inbox_notes_item");
            entity.Property(e => e.Id).HasDefaultValueSql("uuidv7()").HasColumnName("id");
            entity.Property(e => e.ItemKey).HasColumnName("item_key").HasMaxLength(200);
            entity.Property(e => e.Body).HasColumnName("body");
            entity.Property(e => e.AuthorId).HasColumnName("author_id");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()").HasColumnName("created_at");
        });

        modelBuilder.Entity<WorkspaceAdminNote>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("workspace_admin_notes_pkey");
            entity.ToTable("workspace_admin_notes", "workspace");
            entity.HasIndex(e => new { e.WorkspaceId, e.CreatedAt }, "idx_workspace_admin_notes_workspace");

            entity.Property(e => e.Id).HasDefaultValueSql("uuidv7()").HasColumnName("id");
            entity.Property(e => e.WorkspaceId).HasColumnName("workspace_id");
            entity.Property(e => e.Body).HasColumnName("body");
            entity.Property(e => e.AuthorId).HasColumnName("author_id");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()").HasColumnName("created_at");
        });

        modelBuilder.Entity<WorkspaceEntitlementSnapshot>(entity =>
        {
            entity.HasKey(e => e.WorkspaceId).HasName("workspace_entitlement_snapshots_pkey");
            entity.ToTable("workspace_entitlement_snapshots", "workspace");

            entity.Property(e => e.WorkspaceId).HasColumnName("workspace_id");
            entity.Property(e => e.EntitlementsJson)
                .HasColumnType("jsonb")
                .HasDefaultValueSql("'{}'::jsonb")
                .HasColumnName("entitlements");
            entity.Property(e => e.PlanSlug).HasMaxLength(80).HasColumnName("plan_slug");
            entity.Property(e => e.HasActiveSubscription).HasColumnName("has_active_subscription");
            entity.Property(e => e.ResolvedAt).HasColumnName("resolved_at");
            entity.Property(e => e.LastEventId).HasColumnName("last_event_id");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()").HasColumnName("updated_at");
        });

        // The `settings` column is stored as jsonb in PostgreSQL.
        // EF Core maps it to string (scaffolded); serialization/deserialization is
        // handled manually in WorkspaceRepository.GetSettingsAsync / UpdateSettingsAsync.
        modelBuilder.Entity<Workspace>(entity =>
        {
            entity.Property(e => e.Settings)
                .HasColumnType("jsonb")
                .HasDefaultValueSql("'{}'::jsonb");
        });

        modelBuilder.Entity<WorkspaceAdminAction>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("workspace_admin_actions_pkey");
            entity.ToTable("workspace_admin_actions", "workspace");
            entity.HasIndex(e => new { e.WorkspaceId, e.PerformedAt },
                "idx_workspace_admin_actions_workspace");
            entity.HasIndex(e => e.PerformedAt,
                "idx_workspace_admin_actions_performed_at");
            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuidv7()")
                .HasColumnName("id");
            entity.HasIndex(e => new { e.EntityType, e.EntityId, e.PerformedAt },
                "idx_workspace_admin_actions_entity");
            entity.HasIndex(e => new { e.PerformedBy, e.PerformedAt },
                "idx_workspace_admin_actions_actor");
            entity.HasIndex(e => new { e.Action, e.PerformedAt },
                "idx_workspace_admin_actions_action");
            entity.Property(e => e.WorkspaceId).HasColumnName("workspace_id");
            entity.Property(e => e.Action).HasMaxLength(30).HasColumnName("action");
            entity.Property(e => e.SourceService)
                .HasMaxLength(50)
                .HasDefaultValue("workspace-service")
                .HasColumnName("source_service");
            entity.Property(e => e.EntityType)
                .HasMaxLength(50)
                .HasDefaultValue("workspace")
                .HasColumnName("entity_type");
            entity.Property(e => e.EntityId).HasColumnName("entity_id");
            entity.Property(e => e.Result)
                .HasMaxLength(20)
                .HasDefaultValue("succeeded")
                .HasColumnName("result");
            entity.Property(e => e.BeforeSummary).HasColumnType("jsonb").HasColumnName("before_summary");
            entity.Property(e => e.AfterSummary).HasColumnType("jsonb").HasColumnName("after_summary");
            entity.Property(e => e.Reason).HasColumnName("reason");
            entity.Property(e => e.PerformedBy).HasColumnName("performed_by");
            entity.Property(e => e.PerformedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("performed_at");
            entity.Property(e => e.CorrelationId).HasMaxLength(100).HasColumnName("correlation_id");
            entity.Property(e => e.ActorEmail).HasMaxLength(320).HasColumnName("actor_email");
            entity.Property(e => e.ActorName).HasMaxLength(200).HasColumnName("actor_name");
            entity.Property(e => e.EntityKey).HasMaxLength(100).HasColumnName("entity_key");
            entity.Property(e => e.EntityLabel).HasMaxLength(200).HasColumnName("entity_label");
            entity.Property(e => e.ErrorMessage).HasColumnName("error_message");
            entity.Property(e => e.IpAddress).HasMaxLength(64).HasColumnName("ip_address");
            entity.Property(e => e.UserAgent).HasMaxLength(512).HasColumnName("user_agent");
        });
    }
}
