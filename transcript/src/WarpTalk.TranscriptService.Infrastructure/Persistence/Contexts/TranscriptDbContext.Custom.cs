using Microsoft.EntityFrameworkCore;
using WarpTalk.TranscriptService.Domain.Entities;
using WarpTalk.TranscriptService.Domain.Enums;

namespace WarpTalk.TranscriptService.Infrastructure.Persistence.Contexts;

public partial class TranscriptDbContext
{
    partial void OnModelCreatingPartial(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresEnum<TranscriptStatus>("transcript", "transcript_status");
        modelBuilder.HasPostgresEnum<CorrectionStatus>("transcript", "correction_status");
        modelBuilder.HasPostgresEnum<CorrectionType>("transcript", "correction_type");

        modelBuilder.Entity<Transcript>(entity =>
        {
            entity.Property(e => e.Status)
                .HasColumnName("status")
                .HasDefaultValue(TranscriptStatus.Recording);
        });

        modelBuilder.Entity<TranscriptCorrection>(entity =>
        {
            entity.Property(e => e.Status)
                .HasColumnName("status")
                .HasDefaultValue(CorrectionStatus.Pending);

            entity.Property(e => e.CorrectionType)
                .HasColumnName("correction_type");
        });
    }
}
