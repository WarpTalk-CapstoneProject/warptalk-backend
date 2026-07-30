using Microsoft.EntityFrameworkCore;
using WarpTalk.WorkspaceService.Domain.Entities;

namespace WarpTalk.WorkspaceService.Infrastructure.Persistence;

// Safe from re-scaffold: scaffold only writes WorkspaceDbContext.cs, never *.partial.cs.
public partial class WorkspaceDbContext
{
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
    }
}
