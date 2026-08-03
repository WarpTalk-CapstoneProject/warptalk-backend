using Microsoft.EntityFrameworkCore;
using WarpTalk.WorkspaceService.Domain.Entities;

namespace WarpTalk.WorkspaceService.Infrastructure.Persistence;

// Safe from re-scaffold: scaffold only writes WorkspaceDbContext.cs, never *.partial.cs.
public partial class WorkspaceDbContext
{
    public virtual DbSet<WorkspaceAdminAction> WorkspaceAdminActions { get; set; } = null!;

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder)
    {
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
            entity.Property(e => e.WorkspaceId).HasColumnName("workspace_id");
            entity.Property(e => e.Action).HasMaxLength(30).HasColumnName("action");
            entity.Property(e => e.Reason).HasColumnName("reason");
            entity.Property(e => e.PerformedBy).HasColumnName("performed_by");
            entity.Property(e => e.PerformedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("performed_at");
            entity.Property(e => e.CorrelationId).HasMaxLength(100).HasColumnName("correlation_id");
        });
    }
}
