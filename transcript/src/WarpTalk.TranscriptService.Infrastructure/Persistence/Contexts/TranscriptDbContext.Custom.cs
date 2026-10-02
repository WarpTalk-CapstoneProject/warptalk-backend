using Microsoft.EntityFrameworkCore;
using WarpTalk.TranscriptService.Domain.Entities;

namespace WarpTalk.TranscriptService.Infrastructure.Persistence.Contexts;

public partial class TranscriptDbContext
{
    public virtual DbSet<FarSpeakerRelabelJob> FarSpeakerRelabelJobs { get; set; }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder)
    {
        // Bridge far-side relabel. See migration 20261001120100_add_far_speaker_relabel_jobs.
        modelBuilder.Entity<FarSpeakerRelabelJob>(entity =>
        {
            entity.HasKey(e => e.TranslationRoomId).HasName("far_speaker_relabel_jobs_pkey");
            entity.ToTable("far_speaker_relabel_jobs", "transcript");

            entity.Property(e => e.TranslationRoomId).HasColumnName("translation_room_id").ValueGeneratedNever();
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.Attempts).HasColumnName("attempts");
            entity.Property(e => e.NextAttemptAt).HasColumnName("next_attempt_at");
            entity.Property(e => e.RoomEndedAt).HasColumnName("room_ended_at");
            entity.Property(e => e.LastError).HasColumnName("last_error");
            entity.Property(e => e.SegmentsRelabeled).HasColumnName("segments_relabeled");
            entity.Property(e => e.CompletedAt).HasColumnName("completed_at");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
        });
    }
}
