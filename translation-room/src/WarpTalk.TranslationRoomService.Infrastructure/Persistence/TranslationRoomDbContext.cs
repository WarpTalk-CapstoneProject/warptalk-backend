using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using WarpTalk.TranslationRoomService.Domain.Entities;

namespace WarpTalk.TranslationRoomService.Infrastructure.Persistence;

public partial class TranslationRoomDbContext : DbContext
{
    public TranslationRoomDbContext()
    {
    }

    public TranslationRoomDbContext(DbContextOptions<TranslationRoomDbContext> options)
        : base(options)
    {
    }

    public virtual DbSet<SchemaMigration> SchemaMigrations { get; set; }

    public virtual DbSet<SupportedLanguage> SupportedLanguages { get; set; }

    public virtual DbSet<TranslationRoom> TranslationRooms { get; set; }

    public virtual DbSet<TranslationRoomArtifact> TranslationRoomArtifacts { get; set; }

    public virtual DbSet<TranslationRoomAudioRoute> TranslationRoomAudioRoutes { get; set; }

    public virtual DbSet<TranslationRoomFeedback> TranslationRoomFeedbacks { get; set; }

    public virtual DbSet<TranslationRoomParticipant> TranslationRoomParticipants { get; set; }

    public virtual DbSet<UserSetting> UserSettings { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder
            .HasPostgresEnum("artifact_type", new[] { "TRANSCRIPT_EXPORT", "SUMMARY_EXPORT", "DEBUG_LOG", "OPTIONAL_RECORDING", "AUDIO_SAMPLE" })
            .HasPostgresEnum("consent_status", new[] { "GRANTED", "REVOKED", "EXPIRED" })
            .HasPostgresEnum("job_status", new[] { "QUEUED", "PROCESSING", "COMPLETED", "FAILED", "CANCELLED" })
            .HasPostgresEnum("meeting", "media_type", new[] { "AUDIO", "VIDEO" })
            .HasPostgresEnum("meeting", "meeting_status", new[] { "CREATED", "ACTIVE", "FINISHED" })
            .HasPostgresEnum("notification_status", new[] { "PENDING", "SENT", "DELIVERED", "FAILED", "READ" })
            .HasPostgresEnum("participant_status", new[] { "INVITED", "WAITING", "CONNECTED", "DISCONNECTED", "LEFT", "KICKED", "REJECTED" })
            .HasPostgresEnum("room_status", new[] { "SCHEDULED", "WAITING", "IN_PROGRESS", "PAUSED", "ENDED", "CANCELLED", "EXPIRED", "FAILED" })
            .HasPostgresEnum("ticket_status", new[] { "OPEN", "IN_PROGRESS", "RESOLVED", "CLOSED" })
            .HasPostgresEnum("transcript", "correction_status", new[] { "PENDING", "ACCEPTED", "REJECTED" })
            .HasPostgresEnum("transcript", "correction_type", new[] { "STT", "TRANSLATION" })
            .HasPostgresEnum("transcript", "transcript_status", new[] { "RECORDING", "FINALIZING", "FINALIZED", "ARCHIVED" })
            .HasPostgresExtension("uuid-ossp");

        modelBuilder.Entity<SchemaMigration>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("schema_migrations_pkey");

            entity.ToTable("schema_migrations", "translation_room");

            entity.HasIndex(e => e.MigrationKey, "schema_migrations_migration_key_key").IsUnique();

            entity.HasIndex(e => e.Status, "schema_migrations_status_idx");

            entity.HasIndex(e => e.Status, "schema_migrations_status_idx1");

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

        modelBuilder.Entity<SupportedLanguage>(entity =>
        {
            entity
                .HasNoKey()
                .ToView("supported_languages", "translation_room");

            entity.Property(e => e.Code)
                .HasMaxLength(15)
                .HasColumnName("code");
            entity.Property(e => e.IsActive).HasColumnName("is_active");
            entity.Property(e => e.Name)
                .HasMaxLength(100)
                .HasColumnName("name");
            entity.Property(e => e.NativeName)
                .HasMaxLength(100)
                .HasColumnName("native_name");
        });

        modelBuilder.Entity<TranslationRoom>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("translation_rooms_pkey");

            entity.ToTable("translation_rooms", "translation_room", tb => tb.HasComment("Room lifecycle:\nSCHEDULED -> WAITING\nSCHEDULED -> CANCELLED\nSCHEDULED -> EXPIRED\nWAITING -> IN_PROGRESS\nWAITING -> CANCELLED\nWAITING -> EXPIRED\nIN_PROGRESS -> PAUSED\nPAUSED -> IN_PROGRESS\nIN_PROGRESS -> ENDED\nIN_PROGRESS -> FAILED\n\nDraft room is not persisted. If the user discards a draft, no room record is created.\n"));

            entity.HasIndex(e => new { e.HostId, e.CreatedAt }, "translation_rooms_host_id_created_at_idx");

            entity.HasIndex(e => new { e.HostId, e.CreatedAt }, "translation_rooms_host_id_created_at_idx1");

            entity.HasIndex(e => new { e.Status, e.ScheduledAt }, "translation_rooms_status_scheduled_at_idx");

            entity.HasIndex(e => new { e.Status, e.ScheduledAt }, "translation_rooms_status_scheduled_at_idx1");

            entity.HasIndex(e => e.TranslationRoomCode, "translation_rooms_translation_room_code_key").IsUnique();

            entity.HasIndex(e => new { e.WorkspaceId, e.CreatedAt }, "translation_rooms_workspace_id_created_at_idx");

            entity.HasIndex(e => new { e.WorkspaceId, e.CreatedAt }, "translation_rooms_workspace_id_created_at_idx1");

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
            entity.Property(e => e.DurationSeconds).HasColumnName("duration_seconds");
            entity.Property(e => e.EndedAt).HasColumnName("ended_at");
            entity.Property(e => e.HostId)
                .HasComment("External AuthService user id. No physical FK.")
                .HasColumnName("host_id");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true)
                .HasColumnName("is_active");
            entity.Property(e => e.MaxParticipants)
                .HasDefaultValue(10)
                .HasColumnName("max_participants");
            entity.Property(e => e.ScheduledAt).HasColumnName("scheduled_at");
            entity.Property(e => e.Settings)
                .HasDefaultValueSql("'{}'::jsonb")
                .HasColumnType("jsonb")
                .HasColumnName("settings");
            entity.Property(e => e.SourceLanguage)
                .HasMaxLength(15)
                .HasColumnName("source_language");
            entity.Property(e => e.StartedAt).HasColumnName("started_at");
            entity.Property(e => e.Status)
                .HasMaxLength(20)
                .HasDefaultValueSql("'SCHEDULED'::room_status")
                .HasColumnName("status");
            entity.Property(e => e.TargetLanguages)
                .HasDefaultValueSql("'[]'::jsonb")
                .HasColumnType("jsonb")
                .HasColumnName("target_languages");
            entity.Property(e => e.Title)
                .HasMaxLength(255)
                .HasColumnName("title");
            entity.Property(e => e.TranslationRoomCode)
                .HasMaxLength(12)
                .HasColumnName("translation_room_code");
            entity.Property(e => e.TranslationRoomType)
                .HasMaxLength(20)
                .HasDefaultValueSql("'group'::character varying")
                .HasColumnName("translation_room_type");
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

        modelBuilder.Entity<TranslationRoomArtifact>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("translation_room_artifacts_pkey");

            entity.ToTable("translation_room_artifacts", "translation_room");

            entity.HasIndex(e => e.RetentionUntil, "translation_room_artifacts_retention_until_idx");

            entity.HasIndex(e => e.RetentionUntil, "translation_room_artifacts_retention_until_idx1");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuidv7()")
                .HasColumnName("id");
            entity.Property(e => e.ConsentRequired).HasColumnName("consent_required");
            entity.Property(e => e.ContainsRawAudio).HasColumnName("contains_raw_audio");
            entity.Property(e => e.ContainsRawVideo).HasColumnName("contains_raw_video");
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
            entity.Property(e => e.FileFormat)
                .HasMaxLength(20)
                .HasColumnName("file_format");
            entity.Property(e => e.FileSizeBytes).HasColumnName("file_size_bytes");
            entity.Property(e => e.FileUrl)
                .HasMaxLength(500)
                .HasColumnName("file_url");
            entity.Property(e => e.RetentionUntil).HasColumnName("retention_until");
            entity.Property(e => e.Status)
                .HasMaxLength(20)
                .HasDefaultValueSql("'active'::character varying")
                .HasColumnName("status");
            entity.Property(e => e.TranslationRoomId).HasColumnName("translation_room_id");

            entity.HasOne(d => d.TranslationRoom).WithMany(p => p.TranslationRoomArtifacts)
                .HasForeignKey(d => d.TranslationRoomId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("translation_room_artifacts_translation_room_id_fkey");
        });

        modelBuilder.Entity<TranslationRoomAudioRoute>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("translation_room_audio_routes_pkey");

            entity.ToTable("translation_room_audio_routes", "translation_room");

            entity.HasIndex(e => new { e.SourceParticipantId, e.TargetParticipantId }, "translation_room_audio_routes_source_participant_id_target__idx");

            entity.HasIndex(e => new { e.SourceParticipantId, e.TargetParticipantId }, "translation_room_audio_routes_source_participant_id_target_idx1");

            entity.HasIndex(e => new { e.TranslationRoomId, e.Status }, "translation_room_audio_routes_translation_room_id_status_idx");

            entity.HasIndex(e => new { e.TranslationRoomId, e.Status }, "translation_room_audio_routes_translation_room_id_status_idx1");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuidv7()")
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.EndedAt).HasColumnName("ended_at");
            entity.Property(e => e.SourceLanguage)
                .HasMaxLength(15)
                .HasColumnName("source_language");
            entity.Property(e => e.SourceParticipantId).HasColumnName("source_participant_id");
            entity.Property(e => e.StartedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("started_at");
            entity.Property(e => e.Status)
                .HasMaxLength(20)
                .HasDefaultValueSql("'active'::character varying")
                .HasColumnName("status");
            entity.Property(e => e.StreamId)
                .HasMaxLength(100)
                .HasColumnName("stream_id");
            entity.Property(e => e.TargetLanguage)
                .HasMaxLength(15)
                .HasColumnName("target_language");
            entity.Property(e => e.TargetParticipantId).HasColumnName("target_participant_id");
            entity.Property(e => e.TranslationRoomId).HasColumnName("translation_room_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");
            entity.Property(e => e.VoiceCloneEnabled).HasColumnName("voice_clone_enabled");

            entity.HasOne(d => d.SourceParticipant).WithMany(p => p.TranslationRoomAudioRouteSourceParticipants)
                .HasForeignKey(d => d.SourceParticipantId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("translation_room_audio_routes_source_participant_id_fkey");

            entity.HasOne(d => d.TargetParticipant).WithMany(p => p.TranslationRoomAudioRouteTargetParticipants)
                .HasForeignKey(d => d.TargetParticipantId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("translation_room_audio_routes_target_participant_id_fkey");

            entity.HasOne(d => d.TranslationRoom).WithMany(p => p.TranslationRoomAudioRoutes)
                .HasForeignKey(d => d.TranslationRoomId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("translation_room_audio_routes_translation_room_id_fkey");
        });

        modelBuilder.Entity<TranslationRoomFeedback>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("translation_room_feedback_pkey");

            entity.ToTable("translation_room_feedback", "translation_room");

            entity.HasIndex(e => new { e.TranslationRoomId, e.UserId }, "translation_room_feedback_translation_room_id_user_id_idx").IsUnique();

            entity.HasIndex(e => new { e.TranslationRoomId, e.UserId }, "translation_room_feedback_translation_room_id_user_id_idx1").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuidv7()")
                .HasColumnName("id");
            entity.Property(e => e.AiSummaryQuality).HasColumnName("ai_summary_quality");
            entity.Property(e => e.AudioQuality).HasColumnName("audio_quality");
            entity.Property(e => e.Comments).HasColumnName("comments");
            entity.Property(e => e.CommunicationInsights)
                .HasColumnType("jsonb")
                .HasColumnName("communication_insights");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.OverallRating).HasColumnName("overall_rating");
            entity.Property(e => e.TranslationQuality).HasColumnName("translation_quality");
            entity.Property(e => e.TranslationRoomId).HasColumnName("translation_room_id");
            entity.Property(e => e.UserId)
                .HasComment("External AuthService user id. No physical FK.")
                .HasColumnName("user_id");
            entity.Property(e => e.VoiceCloneQuality).HasColumnName("voice_clone_quality");

            entity.HasOne(d => d.TranslationRoom).WithMany(p => p.TranslationRoomFeedbacks)
                .HasForeignKey(d => d.TranslationRoomId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("translation_room_feedback_translation_room_id_fkey");
        });

        modelBuilder.Entity<TranslationRoomParticipant>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("translation_room_participants_pkey");

            entity.ToTable("translation_room_participants", "translation_room", tb => tb.HasComment("Participant lifecycle:\nINVITED -> WAITING\nWAITING -> CONNECTED\nWAITING -> REJECTED\nCONNECTED -> DISCONNECTED\nDISCONNECTED -> CONNECTED\nCONNECTED -> LEFT\nCONNECTED -> KICKED\n\nMUTED is not a participant_status. It is represented by is_muted.\n"));

            entity.HasIndex(e => new { e.TranslationRoomId, e.Status }, "translation_room_participants_translation_room_id_status_idx");

            entity.HasIndex(e => new { e.TranslationRoomId, e.Status }, "translation_room_participants_translation_room_id_status_idx1");

            entity.HasIndex(e => new { e.TranslationRoomId, e.UserId }, "translation_room_participants_translation_room_id_user_id_idx");

            entity.HasIndex(e => new { e.TranslationRoomId, e.UserId }, "translation_room_participants_translation_room_id_user_id_idx1");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("uuidv7()")
                .HasColumnName("id");
            entity.Property(e => e.ConnectionType)
                .HasMaxLength(20)
                .HasDefaultValueSql("'webrtc'::character varying")
                .HasColumnName("connection_type");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.DisplayName)
                .HasMaxLength(100)
                .HasColumnName("display_name");
            entity.Property(e => e.IsTranslationAudioEnabled).HasColumnName("is_translation_audio_enabled");
            entity.Property(e => e.IsUsingVoiceClone).HasColumnName("is_using_voice_clone");
            entity.Property(e => e.JoinedAt).HasColumnName("joined_at");
            entity.Property(e => e.LeftAt).HasColumnName("left_at");
            entity.Property(e => e.ListenLanguage)
                .HasMaxLength(15)
                .HasColumnName("listen_language");
            entity.Property(e => e.Role)
                .HasMaxLength(20)
                .HasDefaultValueSql("'participant'::character varying")
                .HasColumnName("role");
            entity.Property(e => e.SpeakLanguage)
                .HasMaxLength(15)
                .HasColumnName("speak_language");
            entity.Property(e => e.Status)
                .HasMaxLength(20)
                .HasDefaultValueSql("'INVITED'::participant_status")
                .HasColumnName("status");
            entity.Property(e => e.TranslationRoomId).HasColumnName("translation_room_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");
            entity.Property(e => e.UserId)
                .HasComment("External AuthService user id. Nullable for guests. No physical FK.")
                .HasColumnName("user_id");

            entity.HasOne(d => d.TranslationRoom).WithMany(p => p.TranslationRoomParticipants)
                .HasForeignKey(d => d.TranslationRoomId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("translation_room_participants_translation_room_id_fkey");
        });

        modelBuilder.Entity<UserSetting>(entity =>
        {
            entity
                .HasNoKey()
                .ToView("user_settings", "translation_room");

            entity.Property(e => e.DefaultListenLanguage)
                .HasMaxLength(15)
                .HasColumnName("default_listen_language");
            entity.Property(e => e.DefaultSpeakLanguage)
                .HasMaxLength(15)
                .HasColumnName("default_speak_language");
            entity.Property(e => e.UserId).HasColumnName("user_id");
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
