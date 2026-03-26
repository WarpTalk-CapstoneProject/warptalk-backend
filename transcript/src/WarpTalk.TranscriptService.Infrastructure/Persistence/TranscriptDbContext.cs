using Microsoft.EntityFrameworkCore;
using WarpTalk.TranscriptService.Domain.Entities;

namespace WarpTalk.TranscriptService.Infrastructure.Persistence;

public class TranscriptDbContext : DbContext
{
    public TranscriptDbContext(DbContextOptions<TranscriptDbContext> options) : base(options) { }

    public DbSet<Transcript> Transcripts => Set<Transcript>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        
        modelBuilder.HasDefaultSchema("transcript");
        
        modelBuilder.Entity<Transcript>(entity =>
        {
            entity.ToTable("transcripts");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.MeetingId).HasColumnName("meeting_id");
            entity.Property(e => e.Version).HasColumnName("version");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.SourceLanguage).HasColumnName("source_language");
            entity.Property(e => e.TotalSegments).HasColumnName("total_segments");
            entity.Property(e => e.TotalDurationMs).HasColumnName("total_duration_ms");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.FinalizedAt).HasColumnName("finalized_at");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
        });
    }
}
