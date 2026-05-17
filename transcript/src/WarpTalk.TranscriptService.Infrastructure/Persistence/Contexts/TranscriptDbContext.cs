using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using WarpTalk.TranscriptService.Domain.Entities;

namespace WarpTalk.TranscriptService.Infrastructure.Persistence.Contexts;

public partial class TranscriptDbContext : DbContext
{
    public TranscriptDbContext()
    {
    }

    public TranscriptDbContext(DbContextOptions<TranscriptDbContext> options)
        : base(options)
    {
    }

    public virtual DbSet<Glossary> Glossaries { get; set; }

    public virtual DbSet<GlossaryTerm> GlossaryTerms { get; set; }

    public virtual DbSet<Transcript> Transcripts { get; set; }

    public virtual DbSet<TranscriptCorrection> TranscriptCorrections { get; set; }

    public virtual DbSet<TranscriptExport> TranscriptExports { get; set; }

    public virtual DbSet<TranscriptSegment> TranscriptSegments { get; set; }

    public virtual DbSet<TranscriptTranslation> TranscriptTranslations { get; set; }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
#warning To protect potentially sensitive information in your connection string, you should move it out of source code. You can avoid scaffolding the connection string by using the Name= syntax to read it from configuration - see https://go.microsoft.com/fwlink/?linkid=2131148. For more guidance on storing connection strings, see https://go.microsoft.com/fwlink/?LinkId=723263.
        => optionsBuilder.UseNpgsql("Host=localhost;Port=5432;Database=warptalk;Username=postgres;Password=CHANGE_ME_STRONG_PASSWORD;Search Path=transcript,public");

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder
            .HasPostgresEnum("artifact_type", new[] { "TRANSCRIPT_EXPORT", "SUMMARY_EXPORT", "DEBUG_LOG", "OPTIONAL_RECORDING", "AUDIO_SAMPLE" })
            .HasPostgresEnum("consent_status", new[] { "GRANTED", "REVOKED", "EXPIRED" })
            .HasPostgresEnum("job_status", new[] { "QUEUED", "PROCESSING", "COMPLETED", "FAILED", "CANCELLED" })
            .HasPostgresEnum("notification_status", new[] { "PENDING", "SENT", "DELIVERED", "FAILED", "READ" })
            .HasPostgresEnum("participant_status", new[] { "INVITED", "WAITING", "CONNECTED", "DISCONNECTED", "LEFT", "KICKED", "REJECTED" })
            .HasPostgresEnum("room_status", new[] { "SCHEDULED", "WAITING", "IN_PROGRESS", "PAUSED", "ENDED", "CANCELLED", "EXPIRED", "FAILED" })
            .HasPostgresEnum("ticket_status", new[] { "OPEN", "IN_PROGRESS", "RESOLVED", "CLOSED" })
            .HasPostgresEnum("transcript", "correction_status", new[] { "PENDING", "ACCEPTED", "REJECTED" })
            .HasPostgresEnum("transcript", "correction_type", new[] { "STT", "TRANSLATION" })
            .HasPostgresEnum("transcript", "transcript_status", new[] { "RECORDING", "FINALIZING", "FINALIZED", "ARCHIVED" })
            .HasPostgresExtension("uuid-ossp");

        modelBuilder.Entity<Glossary>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("glossaries_pkey");

            entity.ToTable("glossaries", "transcript");

            entity.HasIndex(e => new { e.WorkspaceId, e.Name }, "glossaries_workspace_id_name_idx").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuidv7()")
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy)
                .HasComment("External AuthService user id. No physical FK.")
                .HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy)
                .HasComment("External AuthService user id. No physical FK.")
                .HasColumnName("deleted_by");
            entity.Property(e => e.Description).HasColumnName("description");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true)
                .HasColumnName("is_active");
            entity.Property(e => e.Name)
                .HasMaxLength(150)
                .HasColumnName("name");
            entity.Property(e => e.SourceLanguage)
                .HasMaxLength(15)
                .HasColumnName("source_language");
            entity.Property(e => e.TargetLanguage)
                .HasMaxLength(15)
                .HasColumnName("target_language");
            entity.Property(e => e.TermCount).HasColumnName("term_count");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy)
                .HasComment("External AuthService user id. No physical FK.")
                .HasColumnName("updated_by");
            entity.Property(e => e.WorkspaceId)
                .HasComment("External AuthService workspace id. No physical FK.")
                .HasColumnName("workspace_id");
        });

        modelBuilder.Entity<GlossaryTerm>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("glossary_terms_pkey");

            entity.ToTable("glossary_terms", "transcript");

            entity.HasIndex(e => new { e.GlossaryId, e.SourceTerm }, "glossary_terms_glossary_id_source_term_idx").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuidv7()")
                .HasColumnName("id");
            entity.Property(e => e.Context).HasColumnName("context");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy)
                .HasComment("External AuthService user id. No physical FK.")
                .HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy)
                .HasComment("External AuthService user id. No physical FK.")
                .HasColumnName("deleted_by");
            entity.Property(e => e.Domain)
                .HasMaxLength(50)
                .HasColumnName("domain");
            entity.Property(e => e.GlossaryId).HasColumnName("glossary_id");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true)
                .HasColumnName("is_active");
            entity.Property(e => e.Priority)
                .HasDefaultValue(5)
                .HasColumnName("priority");
            entity.Property(e => e.SourceTerm)
                .HasMaxLength(255)
                .HasColumnName("source_term");
            entity.Property(e => e.TargetTerm)
                .HasMaxLength(255)
                .HasColumnName("target_term");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy)
                .HasComment("External AuthService user id. No physical FK.")
                .HasColumnName("updated_by");

            entity.HasOne(d => d.Glossary).WithMany(p => p.GlossaryTerms)
                .HasForeignKey(d => d.GlossaryId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("glossary_terms_glossary_id_fkey");
        });

        modelBuilder.Entity<Transcript>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("transcripts_pkey");

            entity.ToTable("transcripts", "transcript");

            entity.HasIndex(e => new { e.TranslationRoomId, e.Version }, "transcripts_translation_room_id_version_idx").IsUnique();

            entity.HasIndex(e => new { e.WorkspaceId, e.CreatedAt }, "transcripts_workspace_id_created_at_idx");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuidv7()")
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy)
                .HasComment("External AuthService user id. No physical FK.")
                .HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy)
                .HasComment("External AuthService user id. No physical FK.")
                .HasColumnName("deleted_by");
            entity.Property(e => e.FinalizedAt).HasColumnName("finalized_at");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true)
                .HasColumnName("is_active");
            entity.Property(e => e.SourceLanguage)
                .HasMaxLength(15)
                .HasColumnName("source_language");
            entity.Property(e => e.TotalDurationMs).HasColumnName("total_duration_ms");
            entity.Property(e => e.TotalSegments).HasColumnName("total_segments");
            entity.Property(e => e.TranslationRoomId)
                .HasComment("External TranslationRoomService room id. No physical FK.")
                .HasColumnName("translation_room_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy)
                .HasComment("External AuthService user id. No physical FK.")
                .HasColumnName("updated_by");
            entity.Property(e => e.Version)
                .HasDefaultValue(1)
                .HasColumnName("version");
            entity.Property(e => e.WorkspaceId)
                .HasComment("External AuthService workspace id. No physical FK.")
                .HasColumnName("workspace_id");
        });

        modelBuilder.Entity<TranscriptCorrection>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("transcript_corrections_pkey");

            entity.ToTable("transcript_corrections", "transcript");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuidv7()")
                .HasColumnName("id");
            entity.Property(e => e.CorrectedText).HasColumnName("corrected_text");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.OriginalText).HasColumnName("original_text");
            entity.Property(e => e.ReviewedAt).HasColumnName("reviewed_at");
            entity.Property(e => e.ReviewedBy)
                .HasComment("External AuthService user id. No physical FK.")
                .HasColumnName("reviewed_by");
            entity.Property(e => e.SegmentId).HasColumnName("segment_id");
            entity.Property(e => e.TriggeredRetranslation).HasColumnName("triggered_retranslation");
            entity.Property(e => e.UserId)
                .HasComment("External AuthService user id. No physical FK.")
                .HasColumnName("user_id");

            entity.HasOne(d => d.Segment).WithMany(p => p.TranscriptCorrections)
                .HasForeignKey(d => d.SegmentId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("transcript_corrections_segment_id_fkey");
        });

        modelBuilder.Entity<TranscriptExport>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("transcript_exports_pkey");

            entity.ToTable("transcript_exports", "transcript");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuidv7()")
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.FileUrl)
                .HasMaxLength(500)
                .HasColumnName("file_url");
            entity.Property(e => e.Format)
                .HasMaxLength(10)
                .HasColumnName("format");
            entity.Property(e => e.IncludedLanguages)
                .HasDefaultValueSql("'[]'::jsonb")
                .HasColumnType("jsonb")
                .HasColumnName("included_languages");
            entity.Property(e => e.TranscriptId).HasColumnName("transcript_id");
            entity.Property(e => e.UserId)
                .HasComment("External AuthService user id. No physical FK.")
                .HasColumnName("user_id");

            entity.HasOne(d => d.Transcript).WithMany(p => p.TranscriptExports)
                .HasForeignKey(d => d.TranscriptId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("transcript_exports_transcript_id_fkey");
        });

        modelBuilder.Entity<TranscriptSegment>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("transcript_segments_pkey");

            entity.ToTable("transcript_segments", "transcript");

            entity.HasIndex(e => e.SpeakerParticipantId, "transcript_segments_speaker_participant_id_idx");

            entity.HasIndex(e => new { e.TranscriptId, e.SequenceOrder }, "transcript_segments_transcript_id_sequence_order_idx").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuidv7()")
                .HasColumnName("id");
            entity.Property(e => e.Confidence)
                .HasPrecision(5, 4)
                .HasColumnName("confidence");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.EndTimeMs).HasColumnName("end_time_ms");
            entity.Property(e => e.IsCorrected).HasColumnName("is_corrected");
            entity.Property(e => e.OriginalLanguage)
                .HasMaxLength(15)
                .HasColumnName("original_language");
            entity.Property(e => e.OriginalText).HasColumnName("original_text");
            entity.Property(e => e.SequenceOrder).HasColumnName("sequence_order");
            entity.Property(e => e.SpeakerName)
                .HasMaxLength(100)
                .HasColumnName("speaker_name");
            entity.Property(e => e.SpeakerParticipantId)
                .HasComment("External TranslationRoomService participant id. No physical FK.")
                .HasColumnName("speaker_participant_id");
            entity.Property(e => e.StartTimeMs).HasColumnName("start_time_ms");
            entity.Property(e => e.TranscriptId).HasColumnName("transcript_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.Transcript).WithMany(p => p.TranscriptSegments)
                .HasForeignKey(d => d.TranscriptId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("transcript_segments_transcript_id_fkey");
        });

        modelBuilder.Entity<TranscriptTranslation>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("transcript_translations_pkey");

            entity.ToTable("transcript_translations", "transcript");

            entity.HasIndex(e => new { e.SegmentId, e.TargetLanguage }, "transcript_translations_segment_id_target_language_idx").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuidv7()")
                .HasColumnName("id");
            entity.Property(e => e.Confidence)
                .HasPrecision(5, 4)
                .HasColumnName("confidence");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.IsRetranslated).HasColumnName("is_retranslated");
            entity.Property(e => e.LatencyMs).HasColumnName("latency_ms");
            entity.Property(e => e.SegmentId).HasColumnName("segment_id");
            entity.Property(e => e.TargetLanguage)
                .HasMaxLength(15)
                .HasColumnName("target_language");
            entity.Property(e => e.TranslatedText).HasColumnName("translated_text");
            entity.Property(e => e.TranslatorModel)
                .HasMaxLength(100)
                .HasColumnName("translator_model");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.Segment).WithMany(p => p.TranscriptTranslations)
                .HasForeignKey(d => d.SegmentId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("transcript_translations_segment_id_fkey");
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
