using Microsoft.EntityFrameworkCore;
using WarpTalk.TranscriptService.Domain.Entities;
using WarpTalk.TranscriptService.Domain.Enums;

namespace WarpTalk.TranscriptService.Infrastructure.Persistence.Contexts;

public partial class TranscriptDbContext
{
    partial void OnModelCreatingPartial(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Transcript>(entity =>
        {
            entity.Property(e => e.Status)
                .HasColumnName("status")
                .HasDefaultValue("RECORDING");
        });

        modelBuilder.Entity<TranscriptCorrection>(entity =>
        {
            entity.Property(e => e.Status)
                .HasColumnName("status")
                .HasDefaultValue("PENDING");

            entity.Property(e => e.CorrectionType)
                .HasColumnName("correction_type");
        });
    }
}
