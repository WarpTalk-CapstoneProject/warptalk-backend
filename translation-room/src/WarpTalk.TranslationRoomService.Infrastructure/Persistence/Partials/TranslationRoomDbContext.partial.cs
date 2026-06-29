using Microsoft.EntityFrameworkCore;
using WarpTalk.TranslationRoomService.Domain.Entities;

namespace WarpTalk.TranslationRoomService.Infrastructure.Persistence;

// Safe from re-scaffold: scaffold only writes TranslationRoomDbContext.cs, never files under Partials/.
public partial class TranslationRoomDbContext
{
    partial void OnModelCreatingPartial(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TranslationRoomArtifact>(entity =>
        {
            entity.Property(e => e.ArtifactType).HasColumnName("artifact_type");
        });

        // `settings` and `target_languages` are jsonb columns.
        // Serialization is handled manually in TranslationRoomMapper / LanguageHelper.
        modelBuilder.Entity<TranslationRoom>(entity =>
        {
            entity.Property(e => e.Settings)
                .HasColumnType("jsonb");

            entity.Property(e => e.TargetLanguages)
                .HasColumnType("jsonb");
        });
    }
}
