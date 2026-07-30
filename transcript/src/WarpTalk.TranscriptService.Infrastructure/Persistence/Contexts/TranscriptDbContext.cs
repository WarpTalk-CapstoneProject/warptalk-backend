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

    public virtual DbSet<GlobalGlossaryTerm> GlobalGlossaryTerms { get; set; }

    public virtual DbSet<GlobalGlossaryAudit> GlobalGlossaryAudits { get; set; }

    public virtual DbSet<SchemaMigration> SchemaMigrations { get; set; }

    public virtual DbSet<Transcript> Transcripts { get; set; }

    public virtual DbSet<TranscriptCorrection> TranscriptCorrections { get; set; }

    public virtual DbSet<TranscriptExport> TranscriptExports { get; set; }

    public virtual DbSet<TranscriptSegment> TranscriptSegments { get; set; }

    public virtual DbSet<TranslationContent> TranslationContents { get; set; }

    public virtual DbSet<SegmentTranslationLink> SegmentTranslationLinks { get; set; }

    public virtual DbSet<AudioDubbing> AudioDubbings { get; set; }

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
            entity.Property(e => e.Definition).HasColumnName("definition");
            entity.Property(e => e.UsageNote).HasColumnName("usage_note");
            entity.Property(e => e.PartOfSpeech)
                .HasMaxLength(50)
                .HasColumnName("part_of_speech");
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

        modelBuilder.Entity<GlobalGlossaryTerm>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("global_glossary_terms_pkey");

            entity.ToTable("global_glossary_terms", "transcript");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuidv7()")
                .HasColumnName("id");
            entity.Property(e => e.Term)
                .HasMaxLength(255)
                .HasColumnName("term");
            entity.Property(e => e.PreferredTranslation)
                .HasMaxLength(255)
                .HasColumnName("preferred_translation");
            entity.Property(e => e.SourceLanguage)
                .HasMaxLength(15)
                .HasColumnName("source_language");
            entity.Property(e => e.TargetLanguage)
                .HasMaxLength(15)
                .HasColumnName("target_language");
            entity.Property(e => e.BusinessDomain)
                .HasMaxLength(100)
                .HasColumnName("business_domain");
            entity.Property(e => e.Definition).HasColumnName("definition");
            entity.Property(e => e.UsageNote).HasColumnName("usage_note");
            entity.Property(e => e.Priority)
                .HasDefaultValue(5)
                .HasColumnName("priority");
            entity.Property(e => e.Status)
                .HasMaxLength(30)
                .HasDefaultValue("draft")
                .HasColumnName("status");
            entity.Property(e => e.Version)
                .HasDefaultValue(1)
                .HasColumnName("version");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
        });

        modelBuilder.Entity<GlobalGlossaryAudit>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("global_glossary_audits_pkey");

            entity.ToTable("global_glossary_audits", "transcript");

            entity.HasIndex(e => e.TermId, "idx_global_glossary_audits_term_id");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuidv7()")
                .HasColumnName("id");
            entity.Property(e => e.TermId).HasColumnName("term_id");
            entity.Property(e => e.Action)
                .HasMaxLength(30)
                .HasColumnName("action");
            entity.Property(e => e.BeforeJson)
                .HasColumnType("jsonb")
                .HasColumnName("before_json");
            entity.Property(e => e.AfterJson)
                .HasColumnType("jsonb")
                .HasColumnName("after_json");
            entity.Property(e => e.ActorUserId).HasColumnName("actor_user_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
        });

        modelBuilder.Entity<SchemaMigration>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("schema_migrations_pkey");

            entity.ToTable("schema_migrations", "transcript");

            entity.HasIndex(e => e.MigrationKey, "schema_migrations_migration_key_key").IsUnique();

            entity.HasIndex(e => e.Status, "schema_migrations_status_idx");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuidv7()")
                .HasColumnName("id");
            entity.Property(e => e.AppliedBy)
                .HasMaxLength(100)
                .HasColumnName("applied_by");
            entity.Property(e => e.Checksum)
                .HasMaxLength(128)
                .HasColumnName("checksum");
            entity.Property(e => e.CompletedAt).HasColumnName("completed_at");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.ErrorMessage).HasColumnName("error_message");
            entity.Property(e => e.ExecutionTimeMs).HasColumnName("execution_time_ms");
            entity.Property(e => e.MigrationKey)
                .HasMaxLength(150)
                .HasColumnName("migration_key");
            entity.Property(e => e.MigrationName)
                .HasMaxLength(255)
                .HasColumnName("migration_name");
            entity.Property(e => e.ScriptPath)
                .HasMaxLength(500)
                .HasColumnName("script_path");
            entity.Property(e => e.StartedAt).HasColumnName("started_at");
            entity.Property(e => e.Status)
                .HasMaxLength(20)
                .HasDefaultValueSql("'success'::character varying")
                .HasColumnName("status");
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
            entity.Property(e => e.Status)
                .HasMaxLength(255)
                .HasDefaultValueSql("'RECORDING'::character varying")
                .HasColumnName("status");
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
            entity.Property(e => e.IsCurrent)
                .HasDefaultValue(true)
                .HasColumnName("is_current");
            entity.Property(e => e.PreviousTranscriptId).HasColumnName("previous_transcript_id");
            entity.Property(e => e.EngineVersion).HasMaxLength(50).HasColumnName("engine_version");
            entity.Property(e => e.LastSequenceOrder)
                .HasDefaultValue(0)
                .HasColumnName("last_sequence_order");

            entity.HasOne<Transcript>()
                .WithMany()
                .HasForeignKey(e => e.PreviousTranscriptId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("transcripts_previous_transcript_id_fkey");
        });

        modelBuilder.Entity<TranscriptCorrection>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("transcript_corrections_pkey");

            entity.ToTable("transcript_corrections", "transcript");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuidv7()")
                .HasColumnName("id");
            entity.Property(e => e.CorrectedText).HasColumnName("corrected_text");
            entity.Property(e => e.CorrectionType)
                .HasMaxLength(255)
                .HasColumnName("correction_type");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.OriginalText).HasColumnName("original_text");
            entity.Property(e => e.ReviewedAt).HasColumnName("reviewed_at");
            entity.Property(e => e.ReviewedBy)
                .HasComment("External AuthService user id. No physical FK.")
                .HasColumnName("reviewed_by");
            entity.Property(e => e.SegmentId).HasColumnName("segment_id");
            entity.Property(e => e.Status)
                .HasMaxLength(255)
                .HasDefaultValueSql("'PENDING'::character varying")
                .HasColumnName("status");
            entity.Property(e => e.TriggeredRetranslation).HasColumnName("triggered_retranslation");
            entity.Property(e => e.UserId)
                .HasComment("External AuthService user id. No physical FK.")
                .HasColumnName("user_id");
            entity.Property(e => e.TranslationContentId).HasColumnName("translation_content_id");
            entity.Property(e => e.ReversalCreditTransactionId)
                .HasComment("Soft ref -> subscription.credit_transactions, no physical FK (cross-service).")
                .HasColumnName("reversal_credit_transaction_id");

            entity.HasOne(d => d.Segment).WithMany(p => p.TranscriptCorrections)
                .HasForeignKey(d => d.SegmentId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("transcript_corrections_segment_id_fkey");

            entity.HasOne(d => d.TranslationContent).WithMany()
                .HasForeignKey(d => d.TranslationContentId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("transcript_corrections_translation_content_id_fkey");
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
            entity.Property(e => e.IsFinal)
                .HasDefaultValue(true)
                .HasColumnName("is_final");
            entity.Property(e => e.MatchedSegmentId).HasColumnName("matched_segment_id");

            entity.HasOne(d => d.Transcript).WithMany(p => p.TranscriptSegments)
                .HasForeignKey(d => d.TranscriptId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("transcript_segments_transcript_id_fkey");

            entity.HasOne<TranscriptSegment>()
                .WithMany()
                .HasForeignKey(e => e.MatchedSegmentId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("transcript_segments_matched_segment_id_fkey");
        });

        modelBuilder.Entity<TranslationContent>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("translation_contents_pkey");

            entity.ToTable("translation_contents", "transcript");

            entity.HasIndex(e => new { e.WorkspaceId, e.TextHash, e.TargetLanguage }, "translation_contents_dedup_idx").IsUnique();

            entity.Property(e => e.Id).HasDefaultValueSql("uuidv7()").HasColumnName("id");
            entity.Property(e => e.WorkspaceId)
                .HasComment("External AuthService workspace id. No physical FK.")
                .HasColumnName("workspace_id");
            entity.Property(e => e.TextHash).HasMaxLength(64).HasColumnName("text_hash");
            entity.Property(e => e.TargetLanguage).HasMaxLength(15).HasColumnName("target_language");
            entity.Property(e => e.TranslatedText).HasColumnName("translated_text");
            entity.Property(e => e.TranslatorModel).HasMaxLength(100).HasColumnName("translator_model");
            entity.Property(e => e.Confidence).HasPrecision(5, 4).HasColumnName("confidence");
            entity.Property(e => e.IsRetranslated).HasDefaultValue(false).HasColumnName("is_retranslated");
            entity.Property(e => e.PreviousTranslationContentId).HasColumnName("previous_translation_content_id");
            entity.Property(e => e.LatencyMs).HasColumnName("latency_ms");
            entity.Property(e => e.Status).HasMaxLength(20).HasDefaultValue("pending").HasColumnName("status");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()").HasColumnName("created_at");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()").HasColumnName("updated_at");

            entity.HasOne(d => d.PreviousTranslationContent).WithMany()
                .HasForeignKey(d => d.PreviousTranslationContentId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("translation_contents_previous_content_id_fkey");
        });

        modelBuilder.Entity<SegmentTranslationLink>(entity =>
        {
            entity.HasKey(e => new { e.SegmentId, e.TranslationContentId }).HasName("segment_translation_links_pkey");

            entity.ToTable("segment_translation_links", "transcript");

            entity.HasIndex(e => new { e.SegmentId, e.TargetLanguage })
                .HasDatabaseName("segment_translation_links_current_unique_idx")
                .IsUnique()
                .HasFilter("is_current");

            entity.Property(e => e.SegmentId).HasColumnName("segment_id");
            entity.Property(e => e.TranslationContentId).HasColumnName("translation_content_id");
            entity.Property(e => e.TargetLanguage).HasMaxLength(15).HasColumnName("target_language");
            entity.Property(e => e.IsCurrent).HasDefaultValue(true).HasColumnName("is_current");
            entity.Property(e => e.DeliveredAt).HasColumnName("delivered_at");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()").HasColumnName("created_at");

            entity.HasOne(d => d.Segment).WithMany(p => p.SegmentTranslationLinks)
                .HasForeignKey(d => d.SegmentId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("segment_translation_links_segment_id_fkey");

            entity.HasOne(d => d.TranslationContent).WithMany(p => p.SegmentTranslationLinks)
                .HasForeignKey(d => d.TranslationContentId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("segment_translation_links_content_id_fkey");
        });

        modelBuilder.Entity<AudioDubbing>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("audio_dubbings_pkey");

            entity.ToTable("audio_dubbings", "transcript");

            entity.HasIndex(e => new { e.WorkspaceId, e.TextHash, e.ProviderVoiceId }, "audio_dubbings_dedup_idx").IsUnique();

            entity.HasIndex(e => e.TranslationContentId, "audio_dubbings_translation_idx");

            entity.Property(e => e.Id).HasDefaultValueSql("uuidv7()").HasColumnName("id");
            entity.Property(e => e.WorkspaceId)
                .HasComment("External AuthService workspace id. No physical FK.")
                .HasColumnName("workspace_id");
            entity.Property(e => e.TranslationContentId).HasColumnName("translation_content_id");
            entity.Property(e => e.TextHash).HasMaxLength(64).HasColumnName("text_hash");
            entity.Property(e => e.VoiceType).HasMaxLength(20).HasColumnName("voice_type");
            entity.Property(e => e.Provider).HasMaxLength(50).HasDefaultValue("cartesia").HasColumnName("provider");
            entity.Property(e => e.ProviderVoiceId).HasMaxLength(255).HasColumnName("provider_voice_id");
            entity.Property(e => e.PreviousAudioDubbingId).HasColumnName("previous_audio_dubbing_id");
            entity.Property(e => e.AudioUrl).HasMaxLength(500).HasColumnName("audio_url");
            entity.Property(e => e.DurationMs).HasColumnName("duration_ms");
            entity.Property(e => e.Status).HasMaxLength(20).HasDefaultValue("pending").HasColumnName("status");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()").HasColumnName("created_at");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()").HasColumnName("updated_at");

            entity.HasOne(d => d.TranslationContent).WithMany(p => p.AudioDubbings)
                .HasForeignKey(d => d.TranslationContentId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("audio_dubbings_translation_content_id_fkey");

            entity.HasOne(d => d.PreviousAudioDubbing).WithMany()
                .HasForeignKey(d => d.PreviousAudioDubbingId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("audio_dubbings_previous_dubbing_id_fkey");
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
